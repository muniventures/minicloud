local claims = std.extVar('claims');
if std.objectHas(claims, 'email') && std.objectHas(claims, 'email_verified') && claims.email_verified == true then {
  identity: {
    traits: {
      email: claims.email,
    },
  },
} else error 'Google requires a verified email.'
