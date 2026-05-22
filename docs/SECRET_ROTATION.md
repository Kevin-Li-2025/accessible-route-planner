# Secret Rotation

## Immediate Rotation Needed

The repository previously tracked a real `.env`. Treat every value that appeared there as exposed:

- database passwords and URLs
- Vercel/Postgres connection strings
- any copied local API secrets

Rotate those credentials in the upstream provider, then replace local `.env` values. The tracked `.env` has been removed and ignore rules now prevent re-adding it, but Git history still contains the old values.

## JWT Signing Key Rotation

The API now supports a current signing key plus previous keys:

- `Jwt__Key`: current signing key used for newly issued access tokens
- `Jwt__PreviousKeys`: comma- or semicolon-separated previous keys accepted for validation only

Rotation sequence:

1. Generate a new high-entropy `Jwt__Key`.
2. Move the old `Jwt__Key` into `Jwt__PreviousKeys`.
3. Deploy all API instances.
4. Wait longer than `Jwt__AccessTokenExpirationMinutes`.
5. Remove the old key from `Jwt__PreviousKeys`.

The development placeholder key is rejected outside `Development`.

## Production Secret Source

The API can load mounted key-per-file secrets from `Secrets__KeyPerFilePath` or
`ACCESSCITY_SECRETS_PATH` (default `/mnt/secrets`). In Kubernetes, prefer External Secrets Operator
or the cloud provider's CSI secret driver so rotation happens in the platform secret manager, not in
Git.

The Kubernetes templates expect a Secret named `accesscity-api-secrets`; see
`deploy/kubernetes/external-secret.example.yaml` for the mapping from cloud secret keys to app
configuration keys.

Minimum production secrets:

- `DATABASE_URL`
- `ConnectionStrings__Redis`
- `Jwt__Key`
- `Jwt__PreviousKeys`
- external API keys such as `OpenWeather__ApiKey` and `GooglePlaces__ApiKey`

Rotation sequence for platform secrets:

1. Write the new value in the cloud secret manager.
2. Keep the previous JWT key in `Jwt__PreviousKeys` until all access tokens have expired.
3. Restart pods or let the secret driver refresh mounted files.
4. Check `/health/ready`, auth login, and token refresh.
5. Remove retired keys after the expiration window.

## Refresh Tokens

Refresh tokens are now stored as SHA-256 hashes in the existing `refresh_token.token` column. The raw refresh token is returned to the client once and is never persisted.

Legacy raw tokens are still accepted temporarily during refresh/revoke lookups so existing sessions can rotate naturally. Once active sessions have expired, the compatibility branch can be removed.
