ui = false
disable_mlock = true

storage "file" {
  path = "/openbao/data"
}

listener "tcp" {
  address         = "0.0.0.0:8200"
  cluster_address = "0.0.0.0:8201"
  tls_cert_file   = "/run/secrets/openbao-server.crt"
  tls_key_file    = "/run/secrets/openbao-server.key"
  tls_min_version = "tls13"
}

api_addr     = "https://openbao:8200"
cluster_addr = "https://openbao:8201"
