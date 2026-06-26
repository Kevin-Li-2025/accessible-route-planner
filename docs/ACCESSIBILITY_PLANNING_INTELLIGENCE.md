# Accessibility Planning Intelligence

AccessCity includes a deterministic planning-intelligence path for accessibility data quality, field-verification prioritization, and counterfactual repair analysis. It is designed as decision support for operators and city planners, not as an autonomous route-changing AI system.

## Endpoint

```http
POST /api/v1/planning/accessibility-quality
```

Request:

```json
{
  "minLatitude": 52.47,
  "minLongitude": -1.91,
  "maxLatitude": 52.49,
  "maxLongitude": -1.89,
  "profile": "manual-wheelchair",
  "maxCandidates": 20
}
```

The response contains:

- area-level accessibility metadata quality,
- missing `surface`, `smoothness`, and `width` shares,
- barrier/stairs and high-penalty shares,
- ranked repair or field-verification candidates,
- deterministic AI-style review priority,
- quant-style accessibility alpha, uncertainty, and efficient-frontier rows,
- counterfactual penalty reduction estimates,
- guardrails that state the result is review-only.

## Data Quality Index

The quality index is built from route-edge accessibility metadata already used by the deterministic router:

- `surface`
- `smoothness`
- `width_metres`
- `kerb_height`
- `has_stairs`
- `has_barrier`
- `access`

Area summaries report both simple average quality and distance-weighted quality so a short cluster of weak edges does not dominate a long corridor unless it materially affects traversal coverage.

## Repair Candidate Ranking

Each candidate receives a bounded priority score from:

- metadata gap severity,
- current accessibility penalty for the selected profile,
- estimated counterfactual penalty reduction,
- hard blockers such as stairs, barriers, blocked access, or excessive kerb height.

The counterfactual calculation simulates a reviewed repair state such as known good surface, known smoothness, sufficient clear width, lowered kerb, and removed barrier. It does not write back to the graph and does not affect production routing.

The ranking model is versioned as `accessibility-planning-v2` and is deterministic. It is AI-style triage in the narrow sense of prioritizing review work from multiple signals; it is not a trained black-box model and does not make autonomous routing decisions.

## Quant-Style Frontier Metrics

Repair candidates include:

- `penaltyReductionPer100Metres`: expected mobility penalty reduction normalized by segment length.
- `dataUncertaintyPenalty`: discount for missing or weak metadata.
- `accessibilityAlpha`: excess accessibility improvement after uncertainty discount and blocker lift.
- `reviewPriority`: `critical`, `high`, `medium`, or `low` from the bounded priority score.

The `efficientFrontier` response highlights the strongest candidates by accessibility alpha, uncertainty, and priority. This mirrors a quant research workflow: compare expected improvement against uncertainty and implementation proxy cost instead of ranking by one raw score.

## Human Review Contract

Planning-intelligence output is only a triage layer. A candidate must be confirmed through field survey, trusted municipal data, or reviewed imagery before any OSM or internal route-edge data is changed.

Required review evidence:

- source of verification,
- reviewer identity or service account,
- before/after attribute values,
- timestamp,
- reason for accepting or rejecting the candidate.

## AI Boundary

This feature intentionally does not require an LLM. If AI assistance is added later, it should only help generate candidate explanations, normalize field notes, or propose review checklists. It must not:

- generate route geometry,
- mutate graph edge costs,
- auto-apply OSM tags,
- hide uncertainty or review status,
- tune release thresholds on holdout data.

This matches the broader AccessCity AI contract: model-assisted enrichment is allowed only when deterministic routing and human review remain the authority.

## Quant-Style Evaluation Harness

Treat routing and planning changes like a reproducible research pipeline:

- freeze a benchmark set of route requests,
- record graph artifact schema, edge cost version, and edge weight version,
- compare route distance, estimated time, safety score, warning count, and p95 latency,
- report profile-specific regressions separately,
- keep counterfactual planning improvements separate from production route metrics.

Suggested release gate:

```text
no increase in route failure rate
no unreviewed route-cost mutation
no p95 latency regression above the configured SLO
manual-wheelchair benchmark deltas explained route-by-route
planning candidates reproducible from the same graph snapshot
```

## Route Option Diagnostics

`safe-path/options` returns a `diagnostics` object for the OSRM alternative set when multiple candidates are available. The service keeps the existing weighted recommendation, then reports:

- candidate count,
- Pareto-efficient candidate count,
- recommended time regret versus the fastest candidate,
- recommended risk regret versus the lowest-risk candidate,
- a compact frontier ordered by composite cost.

The frontier treats distance, estimated time, risk exposure, and accessibility penalty as separate objectives. This is intentionally diagnostic: it helps explain trade-offs without replacing the deterministic route-selection policy.

## Current Algorithm Engineering

AccessCity combines several speed-up layers:

- contraction hierarchy for static shortest-path queries,
- ALT lower bounds for safety-weighted A*,
- route graph shard artifacts and corridor loading,
- in-memory hazard risk grids for O(1) per-edge risk lookup,
- spatial buckets for endpoint snapping and nearby graph-node lookup.

The graph snapper first searches nearby spatial buckets and only falls back to a full graph scan when buckets are unavailable or empty. This keeps city-scale endpoint matching aligned with the route graph artifact design instead of doing avoidable linear scans.
