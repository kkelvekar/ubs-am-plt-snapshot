Feature: Infrastructure failure during the write order
    As the Portfolio Snapshot write path
    I need an unreachable downstream dependency during the write order to fail forward
    So that the offset is never committed and no state that would make the snapshot look processed is left behind

    # Mode A (this project) covers the deterministic, Kafka-independent slice of design doc §9: when
    # a downstream dependency is unreachable during the write order (blob → tracking upsert →
    # completeness check → index UPSERT) the exception propagates out of the handler unchanged (so
    # the live consumer would seek-back/retry and never commit the offset) and no durable state is
    # created in the system of record. Each scenario builds a SECOND, fault-injected object graph
    # (real AddApplication/AddInfrastructure wiring, one dependency repointed at an unreachable
    # endpoint) while asserting absence of durable state against the fixture's REAL, reachable
    # resources.
    #
    # The retry cadence / operations-alert half of §9 (0s/5s/30s/30s…, LogCritical at the 3rd
    # consecutive failure) lives in the consumer, not the handler, and is asserted deterministically
    # in the unit test
    # KafkaSnapshotConsumerTests.Retry_delay_sequence_follows_configured_cadence_and_then_holds_at_max.
    # The real broker offset-lag + Critical-alert proof against the live consume/commit path is
    # exercised in Mode B — see tools/fault-injection.ps1.

Scenario: SQL unreachable during the tracking write fails forward and leaves no durable state
    Given a fault-injected graph with SQL repointed to an unreachable endpoint
    And an infra-unavailability snapshot for account "IT-ACC-009"
    When the instruments payload is handled against the fault-injected graph
    Then the handler surfaces an infrastructure error
    And no tracking row exists for the infra-unavailability snapshot
    And no index row exists for the infra-unavailability snapshot

Scenario: ADLS unreachable during the blob write fails forward and writes nothing durable
    Given the infra-unavailability clock starts
    And a fault-injected graph with blob storage repointed to an unreachable endpoint
    And an infra-unavailability snapshot for account "IT-ACC-009"
    When the instruments payload is handled against the fault-injected graph
    Then the handler surfaces an infrastructure error
    And no blob exists under the infra-unavailability snapshot root
    And no tracking row exists for the infra-unavailability snapshot
    And no index row exists for the infra-unavailability snapshot
