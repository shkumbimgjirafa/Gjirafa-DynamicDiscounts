SET NOCOUNT ON;
-- E22: the cost watcher on its first real cost refresh (31 Oct 2026). Reads only PricingToolHackathon's ai tables
-- (aggregates; no price or cost of a single SKU). The replica feed reads costs only for SKUs no other rule owns
-- (dead stock, GjirafaMall prices synced from Gjirafa50 and the holdout categories are left out before the read) that
-- are near their floor; a cost that moved at least 0.5 % from the engine run's becomes an observation, which the watch
-- then judges (CostWatcher.WatchAsync). The replica's rebuild that morning spanned two polls, and the second observed
-- 168 of the first's moves again; they are counted once here (the latest per layer and SKU) and since skipped by the feed.

DECLARE @Day date = '2026-10-31';
WITH o AS (
    SELECT o.*, ROW_NUMBER() OVER (PARTITION BY o.LayerId, o.Sku ORDER BY o.Id DESC) AS rn
    FROM ai.CostObservation o
    WHERE o.Source = 'replica' AND CAST(o.ObservedUtc AS date) = @Day
)
SELECT
    (SELECT COUNT(*) FROM o) AS Observations,
    (SELECT COUNT(*) FROM o WHERE rn > 1) AS RepeatsOfTheSameMove,
    (SELECT COUNT(*) FROM o WHERE rn = 1) AS DistinctMoves,
    (SELECT COUNT(*) FROM o WHERE rn = 1 AND Outcome <> 'not-a-rise') AS Rises,
    (SELECT COUNT(*) FROM o WHERE rn = 1 AND Outcome = 'not-a-rise') AS Falls,
    (SELECT COUNT(*) FROM o WHERE rn = 1 AND Outcome = 'above-floor') AS RisesStillAboveTheFloor,
    (SELECT COUNT(*) FROM o WHERE rn = 1 AND Outcome = 'raised') AS Raised,
    (SELECT COUNT(*) FROM o WHERE rn = 1 AND Outcome = 'held-team-discount') AS HeldTeamDeal,
    (SELECT COUNT(*) FROM o WHERE rn = 1 AND Outcome IN ('held-cost-above-shelf', 'ended-our-discount')) AS HeldCostAboveShelf,
    (SELECT COUNT(*) FROM o WHERE rn = 1 AND Outcome = 'held-data-error') AS HeldDataError,
    (SELECT COUNT(*) FROM o WHERE Outcome LIKE 'held%') AS HoldObservations,
    (SELECT MIN(DATEDIFF(second, ObservedUtc, ProcessedUtc)) FROM o) AS FirstSecondsAfterRefresh,
    (SELECT MAX(DATEDIFF(second, ObservedUtc, ProcessedUtc)) FROM o) AS LastSecondsAfterRefresh,
    (SELECT COUNT(*) FROM o WHERE rn = 1 AND Outcome LIKE '%lane%' OR rn = 1 AND Outcome IN ('synced-to-gj50', 'holdout', 'campaign')) AS InAnotherLane;
