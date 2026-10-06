variable "offline_review" {
  type    = bool
  default = true
}
variable "project_id" {
  type = string
  validation {
    condition     = can(regex("^[a-z][a-z0-9-]{4,28}[a-z0-9]$", var.project_id))
    error_message = "Supply a pre-existing dedicated demo project ID. This module never creates projects."
  }
}
variable "billing_account" { type = string }
variable "project_number" { type = string }
variable "region" {
  type    = string
  default = "us-central1"
}
variable "zone" {
  type    = string
  default = "us-central1-a"
}
variable "prefix" {
  type    = string
  default = "wallet-demo"
  validation {
    condition     = var.prefix == "wallet-demo"
    error_message = "The reviewed host secret manifest and data-device contract use wallet-demo. Review both before changing the prefix."
  }
}
variable "boot_image" {
  type = string
  validation {
    condition     = can(regex("^projects/ubuntu-os-cloud/global/images/ubuntu-2404-", var.boot_image)) && !strcontains(var.boot_image, "/family/")
    error_message = "Supply a reviewed immutable Ubuntu 24.04 image name, not a mutable family."
  }
}
variable "github_repository_id" {
  type = string
  validation {
    condition     = can(regex("^[0-9]+$", var.github_repository_id))
    error_message = "Use GitHub's immutable numeric repository ID."
  }
}
variable "github_owner_id" {
  type = string
  validation {
    condition     = can(regex("^[0-9]+$", var.github_owner_id))
    error_message = "Use GitHub's immutable numeric owner ID."
  }
}
variable "github_repository" {
  type = string
  validation {
    condition     = can(regex("^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$", var.github_repository))
    error_message = "Supply the existing GitHub owner/repository exactly as present in the OIDC workflow_ref claim."
  }
}
variable "demo_cidrs" {
  type = list(string)
  validation {
    condition     = length(var.demo_cidrs) > 0 && alltrue([for c in var.demo_cidrs : can(cidrhost(c, 0)) && !contains(["0.0.0.0/0", "::/0"], c)])
    error_message = "Restrict HTTPS to approved demonstration source CIDRs."
  }
}
variable "monthly_budget_usd" {
  type    = number
  default = 75
  validation {
    condition     = var.monthly_budget_usd > 0 && var.monthly_budget_usd <= 100
    error_message = "Demo budget must be within 1–100 USD; higher amounts require a reviewed code change."
  }
}
variable "protect_vm" {
  type    = bool
  default = true
}
