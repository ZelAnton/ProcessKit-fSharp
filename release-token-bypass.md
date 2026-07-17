# Releasing into a protected `main` — GitHub App token bypass

The release workflow ([.github/workflows/release.yml](.github/workflows/release.yml))
pushes the release commit + tag straight to `main`. Once `main` is protected with a
rule that requires pull requests or status checks, that push is **rejected** unless
the pusher can bypass the rule. This recipe sets up a short-lived GitHub App token
so the release push runs as an App that sits in the ruleset's bypass list.

> Until this is configured, the workflow falls back to the default `GITHUB_TOKEN`
> (the checkout's `token:` is `steps.app-token.outputs.token || secrets.GITHUB_TOKEN`).
> That fallback is fine **while `main` is unprotected** — set this up only when you
> turn on branch protection that requires PRs.

## Why a GitHub App (and not the alternatives)

- **`github-actions[bot]`** — the default Actions identity. It is a system actor,
  **not** an App, so it **cannot** be granted a ruleset bypass. A push as this actor
  stays blocked.
- **A personal access token (PAT)** — works, but is long-lived, tied to one person,
  and needs rotation. Avoid for an unattended release pipeline.
- **A GitHub App installation token** — short-lived (auto-expires at the end of the
  job), not tied to a person, needs no rotation, and an App **can** be added to a
  ruleset's bypass list. This is the supported path.

## One-time setup

1. **Create a GitHub App.**
   - Org repo: **Organization → Settings → Developer settings → GitHub Apps → New GitHub App.**
   - Personal repo: **Settings → Developer settings → GitHub Apps → New GitHub App.**
   - Name it e.g. `<repo>-release-bot`. Homepage URL can be the repo URL.
   - Uncheck **Webhook → Active** (no webhook needed).
   - **Repository permissions → Contents: Read and write** (this is what authorizes
     the push of the release commit + tag). Leave everything else at *No access*.
   - Create the App and note its **App ID**.

2. **Generate a private key.** On the App's page, **Private keys → Generate a private
   key**. A `.pem` file downloads — keep it secret.

3. **Install the App on the repository.** App page → **Install App** → choose the
   account/org → **Only select repositories** → pick this repo.

4. **Wire the credentials into the repo** (Settings → Secrets and variables → Actions):
   - **Variable** `RELEASE_APP_ID` = the App ID from step 1.
   - **Secret** `RELEASE_APP_PRIVATE_KEY` = the full contents of the `.pem` from step 2
     (including the `-----BEGIN/END ...-----` lines).

   The workflow mints the token only when the variable is set
   (`if: ${{ vars.RELEASE_APP_ID != '' }}`), so adding both at once flips it on.

5. **Add the App to the branch-protection bypass list.**
   **Settings → Rules → Rulesets** → open the ruleset protecting `main` →
   **Bypass list → Add bypass → <your App>**. Without this, the App is authenticated
   but still blocked by the rule.

## Verify

> ⚠️ **Do not dispatch the Release workflow just to test the bypass.** A real run
> publishes to NuGet.org — which **cannot be undone** (a published version can only
> be unlisted, never replaced) — and bumps the version and pushes a tag. Verify the
> wiring non-destructively instead:

- **Credentials wired:** the *Mint GitHub App token* step is gated on
  `vars.RELEASE_APP_ID != ''`, so confirm the repo **variable** `RELEASE_APP_ID` and
  the **secret** `RELEASE_APP_PRIVATE_KEY` are both set
  (Settings → Secrets and variables → Actions).
- **App in the bypass list:** Settings → Rules → Rulesets → the `main` ruleset →
  **Bypass list** shows your App.
- **Push permission (optional):** confirm the App can write to `main` out-of-band —
  push a trivial no-op commit to `main` authenticated as the App, or rehearse in a
  throwaway repo carrying the same ruleset. Don't use the production Release workflow
  as the test.

On the **next real release**, the *Mint GitHub App token* step should execute (not
skip) and *Push the release commit + tag (atomic)* should succeed. If that push is
rejected with a protection error, re-check step 5 (the App must be in the ruleset's
bypass list, not merely installed).

## Notes

- The token is installation-scoped and expires when the job ends — there is nothing
  to revoke or rotate.
- The release commit is attributed to the maintainer identity stamped at init
  (`Anton Zhelezniakou` / `github@zelanton.net`), not to the App.
- When configured, the App token authenticates both the **push to `main`** and the
  **GitHub Release** creation (each falls back to the default `GITHUB_TOKEN` when the
  App isn't set up). NuGet publishing is independent of this setup: it uses NuGet
  Trusted Publishing (OIDC), minting a short-lived push key per run via `NuGet/login`
  from the job's `id-token`, so it needs the repo variable `NUGET_USER` plus a
  trusted-publisher policy on nuget.org — not a long-lived `NUGET_API_KEY` secret.
