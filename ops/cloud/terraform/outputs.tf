output "demo_ip" { value = google_compute_address.demo.address }
output "instance" { value = google_compute_instance.demo.name }
output "host_service_account" { value = google_service_account.host.email }
output "deploy_service_account" { value = google_service_account.deploy.email }
output "oidc_provider" { value = google_iam_workload_identity_pool_provider.github.name }
output "backup_bucket" { value = google_storage_bucket.backup.name }
output "secret_ids" { value = { for k, s in google_secret_manager_secret.runtime : k => s.secret_id } }
