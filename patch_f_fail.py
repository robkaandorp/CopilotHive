path = 'tests/CopilotHive.Tests/Worker/WorkerServiceReconnectSurvivalTests.cs'
content = open(path).read()
old = '''            // RUN 2 (the reconnect, ADOPTED).
            plan.StartSecondRun(RegisterResponseFor(adopted: true));
            Assert.Equal("task-A", plan.Invokers[1].Registers[0].CurrentTaskId);

            // THE SECOND RUN ADOPTS but its stream ends before the delivery's Complete write
            // succeeds: the delivery returns to the wait, the run ends with the assignment
            // still Carried (the exit re-check leaves a CARRIED assignment alone).
            plan.CompleteStream();
            await plan.JoinRunAsync();
            Assert.Equal(CarryStates.Carried, GetAssignmentState(GetActiveAssignment(plan.Service)));
            Assert.Empty(plan.CurrentRequests.Completes);'''
new = '''            // RUN 2 (the reconnect, ADOPTED, with the delivery's Complete write FAILING - the
            // stream ends under it).
            plan.StartSecondRun(RegisterResponseFor(adopted: true));
            Assert.Equal("task-A", plan.Invokers[1].Registers[0].CurrentTaskId);
            plan.CurrentRequests.FailNextCompleteWrite = new OperationCanceledException("stream 2 ended");

            // THE SECOND RUN ADOPTS but its stream ends before the delivery's Complete write
            // succeeds: the delivery returns to the wait, the run ends with the assignment
            // still Carried (the exit re-check leaves a CARRIED assignment alone).
            plan.CompleteStream();
            await plan.JoinRunAsync();
            Assert.Equal(CarryStates.Carried, GetAssignmentState(GetActiveAssignment(plan.Service)));
            Assert.Empty(plan.CurrentRequests.Completes);'''
assert content.count(old) == 1, content.count(old)
content = content.replace(old, new)
open(path, 'w').write(content)
print('ok')