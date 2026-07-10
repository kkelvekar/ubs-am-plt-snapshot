// Dev utility: publishes the four sample snapshot envelopes from solution design §4
// (header / instruments / calculations / settings for one snapshotId) onto the
// ubs-advantage-snapshots topic, keyed by accountId.
//
// Bootstrap servers resolution order:
//   1. first command-line argument
//   2. Kafka__BootstrapServers environment variable
//   3. localhost:9092
using Confluent.Kafka;

var bootstrapServers = args.Length > 0
    ? args[0]
    : Environment.GetEnvironmentVariable("Kafka__BootstrapServers") ?? "localhost:9092";
var topic = Environment.GetEnvironmentVariable("Kafka__Topic") ?? "ubs-advantage-snapshots";

const string accountId = "00675442A";

var envelopes = new (string PayloadType, string Json)[]
{
    ("header", """
        {
          "snapshotId":    "corr98765",
          "accountId":     "00675442A",
          "snapshotType":  "portfolio",
          "payloadType":   "header",
          "stage":         "PreTrade",
          "publishedAt":   "2026-05-22T06:14:22Z",
          "publishedBy":   "Portal",
          "schemaVersion": "1.0",
          "payload": {
            "eventType":       "ModelChange",
            "portfolioStatus": "ReadyToSend",
            "orderStatus":     "ReadyToSend",
            "benchmark":       "MCCHM2EQ",
            "baseCcy":         "CHF",
            "orderApprovedBy": "Anna Miller",
            "orderApprovedAt": "2026-05-15T06:10:14Z",
            "orderSentBy":     "James Smith",
            "numOrders":       4,
            "ptcAlerts":       0,
            "programId":       "123456",
            "batchId":         "15884"
          }
        }
        """),
    ("instruments", """
        {
          "snapshotId":    "corr98765",
          "accountId":     "00675442A",
          "snapshotType":  "portfolio",
          "payloadType":   "instruments",
          "stage":         "PreTrade",
          "publishedAt":   "2026-05-22T06:10:14Z",
          "publishedBy":   "PortfolioCalculation",
          "schemaVersion": "1.0",
          "payload": {
            "total": 21,
            "equities": [
              {
                "assetName":     "APPLE LTD",
                "sedol":         "BPBAJ01",
                "ccy":           "CHF",
                "region":        "EMEA",
                "targetPct":     1.52,
                "prevTargetPct": 1.52
              }
            ],
            "futures": [],
            "cash":    []
          }
        }
        """),
    ("calculations", """
        {
          "snapshotId":    "corr98765",
          "accountId":     "00675442A",
          "snapshotType":  "portfolio",
          "payloadType":   "calculations",
          "stage":         "PreTrade",
          "publishedAt":   "2026-05-22T06:11:03Z",
          "publishedBy":   "PortfolioCalculation",
          "schemaVersion": "1.0",
          "payload": {
            "totals": { "marketValue": 1250000.50, "cash": 32000.00 },
            "rows": []
          }
        }
        """),
    ("settings", """
        {
          "snapshotId":    "corr98765",
          "accountId":     "00675442A",
          "snapshotType":  "portfolio",
          "payloadType":   "settings",
          "stage":         "PreTrade",
          "publishedAt":   "2026-05-22T06:11:47Z",
          "publishedBy":   "PortfolioCalculation",
          "schemaVersion": "1.0",
          "payload": {
            "tolerance":     0.25,
            "rebalanceMode": "Full",
            "restrictions":  []
          }
        }
        """),
};

var config = new ProducerConfig
{
    BootstrapServers = bootstrapServers,
    Acks = Acks.All,
};

Console.WriteLine($"Publishing {envelopes.Length} snapshot envelopes to '{topic}' via {bootstrapServers}...");

using var producer = new ProducerBuilder<string, string>(config).Build();

foreach (var (payloadType, json) in envelopes)
{
    var result = await producer.ProduceAsync(
        topic,
        new Message<string, string> { Key = accountId, Value = json });

    Console.WriteLine($"  {payloadType,-13} -> {result.TopicPartitionOffset}");
}

producer.Flush(TimeSpan.FromSeconds(10));
Console.WriteLine("Done.");
