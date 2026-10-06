terraform {
  required_version = "= 1.13.4"
  required_providers {
    google = { source = "hashicorp/google", version = "= 7.17.0" }
  }
}
provider "google" {
  project      = var.project_id
  region       = var.region
  zone         = var.zone
  access_token = var.offline_review ? "offline-review-no-valid-credentials" : null
}
