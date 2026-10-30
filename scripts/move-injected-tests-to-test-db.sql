-- One-off, prompt 05 step 9 ("no mock data in the live DB"), run 2026-10-30: moves the test records out of the live
-- database PricingToolHackathon into PricingToolHackathonTest (created by `PricingTool.AgentTools test-db`), ids kept,
-- in one transaction, then deletes them from the live database:
--   * Alpha's [INJECTED-TEST] pass #3: the pass, its 56 decisions, its AI call and its logged queries;
--   * time-sheet row #2: the sandbox canary publish of prompt 04, rolled back. A test of the publisher, not a task
--     done, so it must not count as hours saved (publish-canary now writes a row only for a live publish that stays).
-- Test runs from now on write to the test database themselves (`--test`).
SET XACT_ABORT ON;
SET NOCOUNT ON;
DECLARE @pass int = 3, @canaryRow bigint = 2;
DECLARE @tag nvarchar(64) = (SELECT CallTag FROM PricingToolHackathon.ai.AgentPass WHERE Id = @pass AND IsTest = 1);
IF @tag IS NULL THROW 50000, 'Pass #3 is not a test pass: nothing moved.', 1;
IF NOT EXISTS (SELECT 1 FROM PricingToolHackathon.ai.TimeSheetEntry WHERE Id = @canaryRow AND Proof = 'ai.PublishBatch #1' AND What LIKE '%sandbox%')
    THROW 50001, 'Time-sheet row #2 is not the sandbox canary: nothing moved.', 1;

SELECT Id INTO #calls FROM PricingToolHackathon.ai.AiCallLog
WHERE CallTag = @tag OR Id IN (SELECT AiCallLogId FROM PricingToolHackathon.ai.AgentPass WHERE Id = @pass);

BEGIN TRAN;

SET IDENTITY_INSERT PricingToolHackathonTest.ai.AiCallLog ON;
INSERT PricingToolHackathonTest.ai.AiCallLog ([Id], [CalledAtUtc], [Agent], [Purpose], [Attempt], [Model], [ModelIds], [InputTokens], [OutputTokens], [CacheReadTokens], [CacheCreationTokens], [LatencyMs], [DurationMs], [DurationApiMs], [NumTurns], [CostUsd], [Success], [Status], [Error], [PromptChars], [Output], [CallTag])
SELECT [Id], [CalledAtUtc], [Agent], [Purpose], [Attempt], [Model], [ModelIds], [InputTokens], [OutputTokens], [CacheReadTokens], [CacheCreationTokens], [LatencyMs], [DurationMs], [DurationApiMs], [NumTurns], [CostUsd], [Success], [Status], [Error], [PromptChars], [Output], [CallTag] FROM PricingToolHackathon.ai.AiCallLog WHERE Id IN (SELECT Id FROM #calls);
PRINT CONCAT('AiCallLog: ', @@ROWCOUNT, ' copied');
SET IDENTITY_INSERT PricingToolHackathonTest.ai.AiCallLog OFF;

SET IDENTITY_INSERT PricingToolHackathonTest.ai.AgentPass ON;
INSERT PricingToolHackathonTest.ai.AgentPass ([Id], [LayerId], [Source], [Mode], [StartedUtc], [FinishedUtc], [Status], [Error], [CallTag], [AiCallLogId], [Applied], [Held], [Rejected], [Pending], [Summary], [IsTest])
SELECT [Id], [LayerId], [Source], [Mode], [StartedUtc], [FinishedUtc], [Status], [Error], [CallTag], [AiCallLogId], [Applied], [Held], [Rejected], [Pending], [Summary], [IsTest] FROM PricingToolHackathon.ai.AgentPass WHERE Id = @pass;
PRINT CONCAT('AgentPass: ', @@ROWCOUNT, ' copied');
SET IDENTITY_INSERT PricingToolHackathonTest.ai.AgentPass OFF;

SET IDENTITY_INSERT PricingToolHackathonTest.ai.AgentDecision ON;
INSERT PricingToolHackathonTest.ai.AgentDecision ([Id], [PassId], [LayerId], [BandId], [Category], [Source], [Action], [CurrentFloorPct], [ProposedFloorPct], [CurrentTargetPct], [ProposedTargetPct], [Confidence], [Reason], [EvidenceRefs], [Status], [Limit], [LimitDetail], [AiCallLogId], [CreatedUtc], [AppliedUtc])
SELECT [Id], [PassId], [LayerId], [BandId], [Category], [Source], [Action], [CurrentFloorPct], [ProposedFloorPct], [CurrentTargetPct], [ProposedTargetPct], [Confidence], [Reason], [EvidenceRefs], [Status], [Limit], [LimitDetail], [AiCallLogId], [CreatedUtc], [AppliedUtc] FROM PricingToolHackathon.ai.AgentDecision WHERE PassId = @pass;
PRINT CONCAT('AgentDecision: ', @@ROWCOUNT, ' copied');
SET IDENTITY_INSERT PricingToolHackathonTest.ai.AgentDecision OFF;

SET IDENTITY_INSERT PricingToolHackathonTest.ai.AgentQueryLog ON;
INSERT PricingToolHackathonTest.ai.AgentQueryLog ([Id], [CalledAtUtc], [Agent], [CallTag], [Tool], [Request], [Accepted], [RowCount], [Truncated], [DurationMs], [Error])
SELECT [Id], [CalledAtUtc], [Agent], [CallTag], [Tool], [Request], [Accepted], [RowCount], [Truncated], [DurationMs], [Error] FROM PricingToolHackathon.ai.AgentQueryLog WHERE CallTag = @tag;
PRINT CONCAT('AgentQueryLog: ', @@ROWCOUNT, ' copied');
SET IDENTITY_INSERT PricingToolHackathonTest.ai.AgentQueryLog OFF;

SET IDENTITY_INSERT PricingToolHackathonTest.ai.TimeSheetEntry ON;
INSERT PricingToolHackathonTest.ai.TimeSheetEntry ([Id], [Date], [UserEmail], [TaskTypeId], [What], [MinutesBefore], [MinutesAfter], [Proof], [Comment], [Source])
SELECT [Id], [Date], [UserEmail], [TaskTypeId], [What], [MinutesBefore], [MinutesAfter], [Proof], [Comment], [Source] FROM PricingToolHackathon.ai.TimeSheetEntry WHERE Id = @canaryRow;
PRINT CONCAT('TimeSheetEntry: ', @@ROWCOUNT, ' copied');
SET IDENTITY_INSERT PricingToolHackathonTest.ai.TimeSheetEntry OFF;

DELETE FROM PricingToolHackathon.ai.TimeSheetEntry WHERE Id = @canaryRow;
PRINT CONCAT('TimeSheetEntry: ', @@ROWCOUNT, ' deleted from the live database');

DELETE FROM PricingToolHackathon.ai.AgentQueryLog WHERE CallTag = @tag;
PRINT CONCAT('AgentQueryLog: ', @@ROWCOUNT, ' deleted from the live database');

DELETE FROM PricingToolHackathon.ai.AgentDecision WHERE PassId = @pass;
PRINT CONCAT('AgentDecision: ', @@ROWCOUNT, ' deleted from the live database');

DELETE FROM PricingToolHackathon.ai.AgentPass WHERE Id = @pass;
PRINT CONCAT('AgentPass: ', @@ROWCOUNT, ' deleted from the live database');

DELETE FROM PricingToolHackathon.ai.AiCallLog WHERE Id IN (SELECT Id FROM #calls);
PRINT CONCAT('AiCallLog: ', @@ROWCOUNT, ' deleted from the live database');

COMMIT;
