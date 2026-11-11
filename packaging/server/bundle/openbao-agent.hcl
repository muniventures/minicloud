pid_file = "/run/minicloud-openbao/agent.pid"

vault {
  address   = "https://openbao:8200"
  ca_cert   = "/run/secrets/openbao-ca.crt"
  tls_skip_verify = false
}

auto_auth {
  method "approle" {
    mount_path = "auth/approle"
    config = {
      role_id_file_path               = "/run/secrets/openbao-role-id"
      secret_id_file_path             = "/run/secrets/openbao-secret-id"
      remove_secret_id_file_after_reading = false
    }
  }

  sink "file" {
    config = {
      path = "/run/minicloud-openbao/token"
      mode = 0640
    }
  }
}
