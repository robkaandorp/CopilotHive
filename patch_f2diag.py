path = 'tests/CopilotHive.Tests/Worker/WorkerServiceReconnectSurvivalTests.cs'
content = open(path).read()
old = '''            plan.StartSecondRun(RegisterResponseFor(adopted: true));
            Assert.Equal("task-A", plan.Invokers[1].Registers[0].CurrentTaskId);

            // THE SECOND RUN ADOPTS'''
new = '''            plan.StartSecondRun(RegisterResponseFor(adopted: true));
            Assert.Equal("task-A", plan.Invokers[1].Registers[0].CurrentTaskId);
            Console.Error.WriteLine(
                "F2DIAG-start occupancy=" + GetSlotOccupancy(plan.Service)
                + " state=" + (GetActiveAssignmentOrNull(plan.Service) is { } o5 ? GetAssignmentState(o5) : -1));

            // THE SECOND RUN ADOPTS'''
assert content.count(old) == 1, content.count(old)
content = content.replace(old, new)
open(path, 'w').write(content)
print('ok')