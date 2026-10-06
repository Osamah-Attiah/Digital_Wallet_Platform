locals {
  apis    = toset(["compute.googleapis.com", "iam.googleapis.com", "iamcredentials.googleapis.com", "sts.googleapis.com", "secretmanager.googleapis.com", "storage.googleapis.com", "billingbudgets.googleapis.com", "logging.googleapis.com", "pubsub.googleapis.com", "iap.googleapis.com", "oslogin.googleapis.com", "cloudresourcemanager.googleapis.com"])
  secrets = toset(["bootstrap_password", "migrator_db_password", "api_db_password", "worker_db_password", "redis_password", "rabbit_password", "rabbit_api_password", "jwt_key", "otp_key", "token_key", "cache_key", "grafana_password", "dependency_tls", "edge_tls", "demo_maker_password", "demo_checker_password", "demo_customer_a_password", "demo_customer_b_password"])
}
resource "google_project_service" "api" {
  for_each           = local.apis
  service            = each.value
  disable_on_destroy = false # APIs may be shared by the pre-existing project.
}
resource "google_compute_network" "demo" {
  name                    = var.prefix
  auto_create_subnetworks = false
  depends_on              = [google_project_service.api]
}
resource "google_compute_subnetwork" "demo" {
  name                     = var.prefix
  ip_cidr_range            = "10.42.0.0/24"
  network                  = google_compute_network.demo.id
  private_ip_google_access = true
}
resource "google_compute_firewall" "https" {
  name          = "${var.prefix}-https"
  network       = google_compute_network.demo.id
  direction     = "INGRESS"
  source_ranges = var.demo_cidrs
  target_tags   = [var.prefix]
  allow {
    protocol = "tcp"
    ports    = ["443"]
  }
}
resource "google_compute_firewall" "iap" {
  name          = "${var.prefix}-iap"
  network       = google_compute_network.demo.id
  direction     = "INGRESS"
  source_ranges = ["35.235.240.0/20"]
  target_tags   = [var.prefix]
  allow {
    protocol = "tcp"
    ports    = ["22"]
  }
}
resource "google_service_account" "host" {
  account_id   = "${var.prefix}-host"
  display_name = "Demo host secret fetch and backup creation only"
  depends_on   = [google_project_service.api]
}
resource "google_service_account" "deploy" {
  account_id   = "${var.prefix}-deploy"
  display_name = "Protected GitHub environment deployer"
  depends_on   = [google_project_service.api]
}
resource "google_compute_address" "demo" {
  name         = var.prefix
  network_tier = "STANDARD"
  depends_on   = [google_project_service.api]
}
resource "google_compute_disk" "data" {
  name       = "${var.prefix}-data"
  type       = "pd-standard"
  size       = 40
  labels     = { environment = "synthetic-demo" }
  depends_on = [google_project_service.api]
}
resource "google_compute_instance" "demo" {
  name                = var.prefix
  machine_type        = "e2-standard-2"
  deletion_protection = var.protect_vm
  tags                = [var.prefix]
  labels              = { environment = "synthetic-demo" }
  boot_disk {
    initialize_params {
      image = var.boot_image
      size  = 20
      type  = "pd-standard"
    }
  }
  attached_disk {
    source      = google_compute_disk.data.id
    device_name = "wallet-data"
  }
  network_interface {
    subnetwork = google_compute_subnetwork.demo.id
    access_config {
      nat_ip       = google_compute_address.demo.address
      network_tier = "STANDARD"
    }
  }
  service_account {
    email  = google_service_account.host.email
    scopes = ["https://www.googleapis.com/auth/cloud-platform"] # IAM below restricts actual permissions.
  }
  metadata = {
    enable-oslogin           = "TRUE"
    block-project-ssh-keys   = "TRUE"
    serial-port-enable       = "FALSE"
    disable-legacy-endpoints = "TRUE"
  }
  shielded_instance_config {
    enable_secure_boot          = true
    enable_vtpm                 = true
    enable_integrity_monitoring = true
  }
  # Bootstrap is deliberately a separate reviewed host operation after infrastructure approval.
  # Never place application credentials, deployment code or terraform secret versions in metadata/state.
}
resource "google_secret_manager_secret" "runtime" {
  for_each  = local.secrets
  secret_id = "${var.prefix}-${replace(each.key, "_", "-")}"
  replication {
    user_managed {
      replicas { location = var.region }
    }
  }
  labels     = { environment = "synthetic-demo" }
  depends_on = [google_project_service.api]
}
resource "google_secret_manager_secret_iam_member" "host" {
  for_each  = local.secrets
  secret_id = google_secret_manager_secret.runtime[each.key].id
  role      = "roles/secretmanager.secretAccessor"
  member    = "serviceAccount:${google_service_account.host.email}"
}
resource "google_storage_bucket" "backup" {
  name                        = "${var.project_id}-${var.prefix}-backup"
  location                    = var.region
  uniform_bucket_level_access = true
  public_access_prevention    = "enforced"
  force_destroy               = false
  soft_delete_policy { retention_duration_seconds = 0 }
  retention_policy {
    retention_period = 86400
    is_locked        = false
  }
  lifecycle_rule {
    condition { age = 7 }
    action { type = "Delete" }
  }
  depends_on = [google_project_service.api]
}
resource "google_storage_bucket_iam_member" "backup_writer" {
  bucket = google_storage_bucket.backup.name
  role   = "roles/storage.objectCreator"
  member = "serviceAccount:${google_service_account.host.email}"
}
resource "google_iam_workload_identity_pool" "github" {
  workload_identity_pool_id = var.prefix
  display_name              = "Protected wallet demo deployment"
  depends_on                = [google_project_service.api]
}
resource "google_iam_workload_identity_pool_provider" "github" {
  workload_identity_pool_id          = google_iam_workload_identity_pool.github.workload_identity_pool_id
  workload_identity_pool_provider_id = "github"
  attribute_mapping = {
    "google.subject"          = "assertion.sub"
    "attribute.repository_id" = "assertion.repository_id"
  }
  attribute_condition = "assertion.repository_id == '${var.github_repository_id}' && assertion.repository_owner_id == '${var.github_owner_id}' && assertion.ref == 'refs/heads/main' && assertion.environment == 'cloud-demo' && assertion.event_name == 'workflow_dispatch' && assertion.workflow_ref == '${var.github_repository}/.github/workflows/cloud-demo.yml@refs/heads/main'"
  oidc { issuer_uri = "https://token.actions.githubusercontent.com" }
}
resource "google_service_account_iam_member" "oidc" {
  service_account_id = google_service_account.deploy.name
  role               = "roles/iam.workloadIdentityUser"
  member             = "principalSet://iam.googleapis.com/${google_iam_workload_identity_pool.github.name}/attribute.repository_id/${var.github_repository_id}"
}
resource "google_compute_instance_iam_member" "oslogin" {
  instance_name = google_compute_instance.demo.name
  role          = "roles/compute.osAdminLogin"
  member        = "serviceAccount:${google_service_account.deploy.email}"
}
resource "google_iap_tunnel_instance_iam_member" "deploy" {
  instance = google_compute_instance.demo.name
  role     = "roles/iap.tunnelResourceAccessor"
  member   = "serviceAccount:${google_service_account.deploy.email}"
}
resource "google_service_account_iam_member" "act_as" {
  service_account_id = google_service_account.host.name
  role               = "roles/iam.serviceAccountUser"
  member             = "serviceAccount:${google_service_account.deploy.email}"
}
resource "google_project_iam_custom_role" "lookup" {
  role_id     = "walletDemoLookup"
  title       = "Wallet demo SSH lookup"
  permissions = ["compute.instances.get", "compute.instances.list", "compute.projects.get", "compute.zones.get", "compute.zones.list"]
}
resource "google_project_iam_member" "lookup" {
  project = var.project_id
  role    = google_project_iam_custom_role.lookup.name
  member  = "serviceAccount:${google_service_account.deploy.email}"
}
resource "google_billing_budget" "demo" {
  billing_account = var.billing_account
  display_name    = "${var.prefix}-monthly"
  budget_filter {
    projects        = ["projects/${var.project_number}"]
    calendar_period = "MONTH"
  }
  amount {
    specified_amount {
      currency_code = "USD"
      units         = tostring(var.monthly_budget_usd)
    }
  }
  threshold_rules { threshold_percent = 0.5 }
  threshold_rules { threshold_percent = 0.8 }
  threshold_rules { threshold_percent = 1.0 }
  all_updates_rule {
    disable_default_iam_recipients = true
    pubsub_topic                   = google_pubsub_topic.budget.id
    schema_version                 = "1.0"
  } # No external notifications without separate consent.
  depends_on = [google_project_service.api]
}
resource "google_logging_project_bucket_config" "logs" {
  project        = var.project_id
  location       = "global"
  bucket_id      = "${var.prefix}-logs"
  retention_days = 7
  depends_on     = [google_project_service.api]
}

resource "google_pubsub_topic" "budget" {
  name                       = "${var.prefix}-budget"
  message_retention_duration = "86400s"
  depends_on                 = [google_project_service.api]
}
