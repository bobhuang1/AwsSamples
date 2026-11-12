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

# Suppressed on purpose: every function runs the dotnet8 managed runtime, which AWS
# deprecates on 2026-11-10 (existing functions keep running; creating and updating them
# is blocked in stages afterwards). The samples are .NET 8 projects (net8.0 target in the
# csproj files and the CI test image), so moving to the next runtime is a separate change:
# retarget the projects, the dotnet runtime in terraform and the CI SDK together, then
# delete this rule block so the check guards the new runtime.
rule "aws_lambda_function_deprecated_runtime" {
  enabled = false
}
