# CI/CD

AccessCity uses GitHub Actions as the primary CI/CD path and keeps GitLab CI as a secondary compatibility path.

## CI gates

`.github/workflows/ci.yml` runs on pull requests, pushes to `main`, `master`, and `codex/**`, and manual dispatches.

- Backend: restore, release build, `dotnet format --verify-no-changes`, NuGet vulnerability scan, xUnit integration tests with PostGIS and Redis.
- Frontend: `npm ci`, Expo lint, TypeScript typecheck, `npm audit --audit-level=high`, Expo web export, Jest in CI-safe serial mode, and Playwright web E2E.
- Manifests: Docker Compose config validation, worker/migration/AI profile validation, nginx config validation, and Kubernetes plus capacity-test kustomize rendering.
- Container security: fresh API image build with pulled base layers, then a Grype fixed high/critical vulnerability gate.
- Pull requests also run GitHub dependency review for new high+ dependency risk.
- CI disables live Overpass hazard enrichment so tests remain deterministic and do not depend on external API tail latency.
- Repository hygiene is enforced through a PR template, CODEOWNERS for high-risk surfaces, and issue templates for performance and AI model evidence.
- The manual `Evidence` workflow builds a downloadable proof pack for backend accessibility-planning tests, the AI model evaluation artifact, city-scale low-latency benchmark gates, and optional native/C++ benchmark smoke runs.

## CD path

`.github/workflows/cd.yml` runs on pushes to `main`/`master`, version tags, and manual dispatches.

- Builds the API image from `AccessCity.API/Dockerfile`.
- Pushes immutable branch, tag, SHA, and default-branch `latest` tags to GHCR.
- Publishes SBOM/provenance metadata through Docker Buildx.
- Scans the pushed digest with Grype.
- Renders Kubernetes manifests pinned to the published image digest and uploads them as an artifact.

Manual Kubernetes deploys are disabled by default. To enable them, add a GitHub environment secret named `KUBE_CONFIG_B64` containing a base64-encoded kubeconfig, run the `CD` workflow manually, set `deploy=true`, and choose the target environment. The deploy job applies `deploy/kubernetes` with the just-published image digest and waits for API and worker rollouts.

## Local parity commands

```bash
dotnet restore CodeConquerors.sln
dotnet build CodeConquerors.sln --configuration Release --no-restore
dotnet format CodeConquerors.sln --verify-no-changes --no-restore --verbosity minimal
dotnet test AccessCity.Tests/AccessCity.Tests.csproj --configuration Release --no-build

cd AccessCity.App
npm ci
npm run lint
npx tsc -p tsconfig.json --noEmit
npm audit --audit-level=high
npm run build:web
npm run test:ci
npm run test:e2e

docker compose config --quiet
docker compose --profile worker config --quiet
docker compose --profile migrate config --quiet
docker compose --profile ai config --quiet
kubectl kustomize deploy/kubernetes >/tmp/accesscity-kubernetes.yaml
kubectl kustomize deploy/kubernetes-capacity >/tmp/accesscity-capacity.yaml
docker build --pull -t accesscity-api:ci ./AccessCity.API
docker run --rm -v /var/run/docker.sock:/var/run/docker.sock ghcr.io/anchore/grype:v0.112.0 accesscity-api:ci --only-fixed --fail-on high
```

## Evidence workflow

Run `.github/workflows/evidence.yml` manually when a PR changes benchmark-sensitive routing code, AI/planning intelligence, or public performance claims.

Default mode runs:

- Release build.
- Accessibility-planning tests that emit `TestResults/accesscity-ai-model-eval/accessibility_ranker_eval_report.json`.
- CI-sized city-scale low-latency benchmark gate.
- Evidence manifest with the commit SHA, ref, selected steps, and claim boundary.

Optional native mode also runs the C++ kernel and market-data benchmark scripts when the GitHub runner has the required compiler/toolchain. Treat those numbers as runner-scoped smoke evidence, not a substitute for pinned-host Linux `perf stat`/`perf record` captures.
