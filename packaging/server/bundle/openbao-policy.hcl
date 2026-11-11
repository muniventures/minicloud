path "minicloud-apps/data/env/+/orgs/+/apps/+/services/+/+" {
  capabilities = ["create", "read", "update"]
}

path "minicloud-apps/data/env/+/orgs/+/providers/+/credentials/+" {
  capabilities = ["create", "read", "update"]
}

path "minicloud-apps/metadata/env/+/orgs/+/apps/+/services/+/+" {
  capabilities = ["delete"]
}

path "minicloud-apps/metadata/env/+/orgs/+/providers/+/credentials/+" {
  capabilities = ["delete"]
}

path "auth/token/renew-self" {
  capabilities = ["update"]
}
