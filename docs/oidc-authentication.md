# OpenID Connect authentication

ChaptarrNG supports OIDC Authorization Code sign-in with PKCE for ordinary
providers. Configure it in
**Settings → General → Security** and choose **OpenID Connect** as the
authentication method. This adapter follows the [shared cross-project OIDC
contract](https://github.com/snapetech-org/seerrng/blob/main/docs/maintainers/oidc-cross-project-template.md)
while keeping ChaptarrNG's local authentication and session behavior. The
[provider catalog](https://github.com/snapetech-org/seerrng/blob/main/docs/maintainers/oidc-provider-catalog.md)
lists branded IDs, official setup links, issuer examples, and scope/claim
notes.

Configure the provider's issuer/authority URL, confidential client ID and
secret, and scopes. The default scopes are `openid profile email`; `openid`
and `email` are always required. Register the exact public callback URI with
the provider:

```text
https://chaptarr.example.com/signin-oidc
```

When ChaptarrNG is served under a URL base path, register that path before
`/signin-oidc`. The legacy `BOOKSHELF_OIDC_REDIRECT_URI` environment setting
can override the callback when it is an absolute HTTPS URL ending in
`/signin-oidc`.

Use **Allowed Emails** or **Allowed Email Domains** to control who can sign in.
An empty admission policy denies OIDC sign-in unless **Allow Any Verified OIDC
User** is explicitly enabled. `email_verified=true` is always required and the
legacy `OidcRequireEmailVerified` setting can no longer disable that policy;
the Security settings page shows this rule as enforced and read-only.
allow-any admission also requires a verified email. Group claims do not grant
roles, and OIDC does not silently link a different local account by email.

**Allow Any Verified OIDC User** is a privileged setting: it lets the identity
provider decide who may access ChaptarrNG. Keep it off when the provider does
not enforce an access policy. Existing local sign-in remains available as the
configured recovery path.

Most settings are stored in ChaptarrNG's host configuration. For migrations,
empty settings may fall back to the legacy `BOOKSHELF_OIDC_*` environment
variables; saved ChaptarrNG values take precedence. Public providers should
use HTTPS. Loopback HTTP remains available for local development; explicitly
set `BOOKSHELF_OIDC_ALLOW_PRIVATE_ISSUER=true` when the trusted provider uses
other private-network endpoints.

OIDC discovery and backchannel requests use a 15-second timeout, refuse HTTP
redirects, and cap provider responses at 1 MiB. The browser receives the
normal ChaptarrNG session, not provider tokens. Local logout clears the app
session; it does not sign the user out of the identity provider.

The login page recognizes `CHAPTARR_OIDC_PROVIDER` as an optional presentation
hint (with `BOOKSHELF_OIDC_PROVIDER` as a migration fallback). Use `google`,
`microsoft-entra`, `okta`, `auth0`, `keycloak`,
`authentik`, `zitadel`, `amazon-cognito`, `gitlab`, `fusionauth`, `onelogin`,
`pingone`, `jumpcloud`, or `apple-sign-in` to show the matching
local icon and official setup link. Unknown values use generic OIDC branding.
When the hint is unset, ChaptarrNG infers a preset only from recognizable
issuer host/path patterns. Branding never changes the configured authority or
token validation.

## Enable, verify, and recover

Register the exact public HTTPS callback ending in `/signin-oidc`. Include the
reverse-proxy path if ChaptarrNG is mounted below the origin. The app's saved
ChaptarrNG settings take precedence; the legacy `BOOKSHELF_OIDC_*` environment
values are migration fallbacks when a setting is empty. Protect the server-side
secret source and keep the client secret out of browser settings, checked-in
configuration, and support logs.

Before switching to OIDC, confirm the local sign-in method intended for
recovery is configured and tested. Use a verified email/domain allow-list for
normal admission. **Allow Any Verified OIDC User** admits everyone with a
verified email who can reach the provider application, so enable it only when
the IdP has a matching access policy. Provider groups do not assign roles, and
matching email does not silently link identities.

Test one allowed and one denied identity through the public HTTPS URL. Confirm
the normal ChaptarrNG session is created and that local recovery remains
available. To disable OIDC, switch to the required local authentication mode
in **Settings → General → Security**, clear the provider values, and apply the
change using the normal service configuration process. For secret rotation,
update the provider and ChaptarrNG settings together and repeat the login
checks.

## Troubleshooting

| Symptom | Check |
| --- | --- |
| Provider reports an invalid callback | Match the exact HTTPS origin, optional proxy base path, and `/signin-oidc` suffix in both the saved setting and provider client. |
| Discovery or callback validation fails | Check the exact issuer/authority, tenant or realm, discovery document, CA trust, and network reachability. Private issuers need explicit opt-in; public deployments should use HTTPS. |
| Sign-in reaches ChaptarrNG but is denied | Confirm `email`, affirmative `email_verified`, address/domain allow-list values, and the allow-any setting. Empty admission denies by default. |
| State, nonce, or cookie validation fails | Start a fresh attempt in the same browser and public hostname; check proxy HTTPS forwarding, browser cookie policy, and whether the transaction expired. |
| OIDC settings do not match the environment | Saved ChaptarrNG settings take precedence over legacy `BOOKSHELF_OIDC_*` values. Check the saved value first, then the migration fallback. |
| Apple form callback fails | Verify the Services ID, exact Apple issuer, `/signin-oidc`, signed JWT validity, `openid email`, and form-post cookie behavior. |

For support, include ChaptarrNG's version, provider ID, redacted issuer host,
callback path, error code, and timestamp. Remove codes, tokens, secrets, raw
claims, and complete callback query strings.

## Sign in with Apple

Set the authority to exactly `https://appleid.apple.com`, use the Apple
Services ID as the client ID, and set the client secret to Apple's signed
client-secret JWT. Register the exact HTTPS callback
`https://<public-host>/signin-oidc`. ChaptarrNG requests only `openid email`,
uses `response_mode=form_post`, and omits PKCE for Apple's documented web flow.
Apple may encode `email_verified` as a boolean or string; only `true` is
admitted. Rotate the JWT before its six-month maximum expiry. See Apple's
[environment setup](https://developer.apple.com/documentation/signinwithapple/configuring-your-environment-for-sign-in-with-apple)
and [web authorization flow](https://developer.apple.com/documentation/signinwithapple/incorporating-sign-in-with-apple-into-other-platforms).
