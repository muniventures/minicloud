# Minicloud

Public distribution repository for the Municloud CLI and reusable customer deployment workflows.

## Customer Deploy Workflow

Customer repositories can call the reusable workflow instead of copying the deployment implementation:

```yaml
jobs:
  deploy:
    uses: muniventures/minicloud/.github/workflows/customer-deploy.yml@main
    with:
      customer: teamcore
      app_name: teamcore
      environment: staging
      deployment_type: backend_frontend
      database: postgres
      aspnetcore_environment: Staging
      frontend_context: ./modules/ui/dashboard
      backend_context: .
      backend_dockerfile: modules/api/Dockerfile
    secrets:
      municloud_dispatch_token: ${{ secrets.MUNICLOUD_DISPATCH_TOKEN }}
      postgres_password: ${{ secrets.MUNICLOUD_POSTGRES_PASSWORD }}
```

The caller repository still owns source checkout, Docker image build context, package publishing permissions, and customer secrets. The Municloud runtime repository keeps provider, DNS, SSH, and VPS secrets.

