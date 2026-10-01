# Aws

Sample code for deploying **large-scale web sites on AWS** — serverless-first
architecture with Terraform as the infrastructure-as-code layer and .NET 8
Lambda handlers for the application code. Three scenarios, each self-contained
under its own folder (own Terraform state, own deploy script, own README):

| Sample | What it is | AWS tech on show |
| --- | --- | --- |
| [shopping/](shopping) | High-scale e-commerce: catalog, cart, checkout, order fulfillment | CloudFront + WAF, S3, API Gateway HTTP API + JWT auth (Cognito), Lambda, DynamoDB, SQS + DLQ, Step Functions, EventBridge, CloudWatch |
| [gaming/](gaming) | Real-time game backend: matchmaking, live game events, leaderboard, telemetry | API Gateway HTTP + WebSocket, Lambda, DynamoDB, Kinesis Data Streams, S3 |
| [chat/](chat) | Large-scale realtime chat over WebSockets | API Gateway WebSocket API, Lambda, DynamoDB, SQS fan-out, browser client |

## Why this shape

Every scenario follows the same mental model:

- **Stateless compute everywhere** — no EC2 or containers; Lambda scales
  without operator involvement. Code is plain .NET 8 (managed runtime,
  `lambda-runtime` `dotnet8`, no custom container).
- **Managed data planes** — DynamoDB with PAY_PER_REQUEST billing, TTLs,
  and global secondary indexes instead of instance-managed databases.
- **Async by default** — SQS queues + Dead Letter Queues buffer work;
  Step Functions choreograph multi-step flows; EventBridge fans events out.
- **One deployable per scenario** — `publish.ps1` builds the Lambda zips,
  then `terraform apply` creates everything the scenario needs.

## Deploying any sample

Prerequisites: Terraform 1.6+, .NET 8 SDK, the AWS CLI with credentials
(`aws configure`, or `AWS_PROFILE`), and an S3 bucket for the sample's
Terraform state (or keep local state for learning).

```powershell
cd shopping
./lambdas/publish.ps1      # dotnet publish + zip each handler
cd terraform
terraform init
terraform plan -out plan.tfplan
terraform apply plan.tfplan
```

Inspect `terraform/outputs.tf` for the endpoints, then follow each sample's
README. `terraform destroy` tears everything down.

## Hardening / multi-region notes

The samples are single-region by design so they stay readable. The scale
patterns (serverless, async queues, managed DBs) hold in multi-region too:
put DynamoDB global tables behind the scenes, front everything with a global
CloudFront distribution + WAF, and point API Gateways at regional replicas.
Each README calls out the specific next step for that workload.

## License

This project is free software, released under the **GNU General Public License v3.0**. You may redistribute and/or modify it under those terms; see [LICENSE.md](LICENSE.md) for the full text.
