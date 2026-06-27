# Accessibility Repair Ranker Model Card

## Model Summary

`accessibility-repair-ranker-v1` is a local, deterministic, auditable linear logistic ranker used by `POST /api/v1/planning/accessibility-quality`.

It ranks route-edge accessibility data gaps and repair candidates for human review. It does not generate routes, mutate OSM tags, update edge costs, or approve repairs automatically.

## Intended Use

Use the ranker to prioritize:

- field verification for missing or uncertain accessibility metadata,
- review of likely mobility blockers such as stairs, barriers, blocked access, excessive kerb height, narrow width, and poor surface quality,
- candidate queues where high expected accessibility improvement should be checked before routine low-impact metadata cleanup.

The output is decision support for planners, reviewers, and data-quality workflows.

## Non-Goals

The ranker must not be used to:

- change production route edge weights directly,
- auto-apply map edits,
- infer legal accessibility compliance,
- replace field survey or trusted municipal source review,
- claim city-wide generalization from the checked-in deterministic fixture alone.

## Inputs

The model consumes route-edge features already used by deterministic accessibility routing:

| Feature | Meaning |
| --- | --- |
| `dataGap` | Missing or low-confidence accessibility metadata share. |
| `penaltyReductionPer100Metres` | Estimated mobility penalty reduction after counterfactual repair, normalized by segment length. |
| `blockerScore` | Stairs, barriers, or very high current accessibility penalty. |
| `kerbRisk` | Kerb height likely needs ramp verification. |
| `missingWidth` | Missing clear path width. |
| `missingSmoothness` | Missing smoothness metadata. |
| `missingSurface` | Missing or unknown surface metadata. |
| `distanceLog` | Log-scaled segment distance. |

## Outputs

Each repair candidate includes:

- `modelScore`: bounded ranker score in `[0, 1]`,
- `modelConfidence`: coarse confidence band from versioned calibration thresholds,
- `featureContributions`: per-feature contribution for auditability,
- `activeLearningScore`: uncertainty and information-value score for deciding what to verify next,
- `reviewStrategy`: operational queue such as `high-confidence-repair-review`, `active-learning-field-verification`, `uncertainty-sampling`, or `routine-field-verification`,
- `priorityScore`: final bounded review priority used by the planning endpoint.

## Evaluation

The checked-in evaluation harness is `AccessCity.Tests/AccessibilityPlanningModelEvalTests.cs`.

It builds `accessibility-repair-ranker-fixture-v1`, a deterministic Birmingham-style route-edge metadata fixture with manually assigned relevance labels:

| Label | Meaning |
| ---: | --- |
| 0 | No review needed. |
| 1 | Low information value. |
| 2 | High review value. |
| 3 | Critical accessibility blocker. |

The harness writes:

- `TestResults/accesscity-ai-model-eval/accessibility_ranker_eval_report.json`
- `TestResults/accesscity-ai-model-eval/accessibility_ranker_eval_summary.md`

Current release gates:

| Metric | Gate |
| --- | ---: |
| NDCG@5 | >= 0.9000 |
| Precision@3 | >= 0.6600 |
| Recall@3 | >= 0.5000 |
| Top relevance | 3 |

## Guardrails

- Human or trusted-source verification is required before any map or route-edge data update.
- Counterfactual repair analysis is report-only and does not change routing graph costs.
- Feature contributions must remain visible in API output and evaluation artifacts.
- Public claims must distinguish deterministic fixture regression from held-out city-scale validation.

## Known Limitations

- The checked-in fixture proves reproducibility and ranking behavior, not city-wide statistical generalization.
- Weights are hand-audited and deterministic; they are not trained from a large labeled corpus yet.
- Calibration thresholds are coarse and should be re-estimated from real accepted/rejected review outcomes before production automation.
- Active-learning score estimates information value, but it still needs operational feedback from actual field-verification queues.

## Upgrade Path

The next stronger model milestone is:

1. collect accepted/rejected field-review outcomes,
2. freeze train/validation/test splits by city area and source date,
3. report NDCG, precision/recall, calibration, and subgroup slices,
4. add drift checks for metadata source mix and feature distribution,
5. keep the deterministic ranker as the fallback until the learned model beats it on held-out data.
