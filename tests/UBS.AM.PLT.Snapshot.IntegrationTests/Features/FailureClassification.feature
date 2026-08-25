Feature: Failure classification
    As the Portfolio Snapshot write path
    I need SnapshotRequestCommand to classify every processing exception into transient,
    poison, or handled, rather than letting the org platform library's log-only Fail silently
    skip a message
    So that a real infrastructure outage parks the consumer and restarts the pod for Kafka
    redelivery, while a code defect is durably recorded and committed past instead of blocking
    the partition forever

    # Mode A. These scenarios resolve the real SnapshotRequestCommand directly (bypassing the
    # Kafka consumer entirely, same as InfrastructureFailureDuringWrite.feature bypasses it one
    # layer down at the handler) against a fault-injected object graph built the same way
    # InfrastructureFailureDuringWriteSteps builds its second graph, with a FakeHostApplicationLifetime
    # and a ControllableTimeProvider substituted for the fixture's defaults so the park is
    # observable and releasable instead of a real 30-second wait. Assertions run against the
    # fixture's REAL, reachable Azure SQL and blob resources. Mode B covers the live consumer
    # loop and process-exit behaviour this feature cannot reach.

Scenario: A transient SQL outage parks the consumer and reports failure without writing anything
    Given the failure-classification clock starts
    And a failure-classification snapshot for account "IT-ACC-030"
    And a fault-injected command graph with SQL repointed to an unreachable endpoint
    When the orders request is executed against the fault-injected command
    Then the command execution is still pending
    And the fault-injected host was stopped exactly once with a non-zero exit code
    When the park is released
    Then the fault-injected command reports failure
    And no tracking row exists for the failure-classification snapshot
    And no index row exists for the failure-classification snapshot
    And no blob exists under the failure-classification snapshot root

Scenario: A code defect with infrastructure reachable is recorded as FAILED and committed past
    Given the failure-classification clock starts
    And a failure-classification snapshot for account "IT-ACC-031"
    And a reachable command graph with blob writes replaced by a simulated code defect
    When the orders request is executed against the fault-injected command
    Then the fault-injected command reports success
    And the failure-classification host was never stopped
    And the failure-classification snapshot has a FAILED tracking row with reason UNEXPECTED_ERROR
    And a Failed response was published for the failure-classification snapshot
    And no index row exists for the failure-classification snapshot
    And no blob exists under the failure-classification snapshot root

Scenario: A poison snapshot recovers to RECEIVING once a valid payload arrives through the normal graph
    Given the failure-classification clock starts
    And a failure-classification snapshot for account "IT-ACC-032"
    And a reachable command graph with blob writes replaced by a simulated code defect
    When the orders request is executed against the fault-injected command
    Then the fault-injected command reports success
    And the failure-classification snapshot has a FAILED tracking row with reason UNEXPECTED_ERROR
    When a required orders payload is delivered for the failure-classification snapshot through the fixture handler
    Then the failure-classification snapshot has returned to RECEIVING
