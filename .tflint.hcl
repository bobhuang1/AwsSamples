# tflint configuration shared by the three samples (run from each sample's terraform folder):
#   tflint --init --config ../../.tflint.hcl
#   tflint --config ../../.tflint.hcl
config {
  call_module_type = "local"
}

plugin "terraform" {
  enabled = true
  preset  = "recommended"
}

plugin "aws" {
  enabled = true
  version = "0.50.0"
  source  = "github.com/terraform-linters/tflint-ruleset-aws"
}
