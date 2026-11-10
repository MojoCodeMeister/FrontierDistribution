using Frontier.Delivery;
using System.Text.Json;

Console.WriteLine("BUSINESS SYSTEM: Transportation / Distribution");
Console.WriteLine("BUSINESS SOFTWARE: Route4Me (delivery execution); ERP (shipment/order system of record)");
Console.WriteLine("PROCESS: Dispatch-to-Delivery | COMPONENT: Delivery Execution and Confirmation");
Console.WriteLine("INTEGRATION: ERP/Shipping -> Route4Me delivery outcome -> ERP/Customer Service (conceptual)");
Console.WriteLine("IMPLEMENTATION: Frontier.Delivery | HARNESS: Frontier.Delivery.Tests");
Console.WriteLine();
int passed = 0, failed = 0;
void Check(string label, bool ok) { Console.WriteLine($"{(ok ? "PASS" : "FAIL")} [Route4Me/Delivery] {label}"); if (ok) passed++; else failed++; }
var svc = new DeliveryExecution();
var lines = new[] { new ShipmentLine("P000142",3), new ShipmentLine("P000143",2) };
var shipment = new DispatchedShipment("SHIP-0001", "SO-0001", "WH-01", "Dispatched", lines);
var early = new DispatchedShipment("SHIP-PREP", "SO-0002", "WH-01", "Prepared", lines);
var occurredAt = new DateTimeOffset(2026,11,9,12,0,0,TimeSpan.FromHours(-5));
DeliveryEvent Delivered(string id="DEL-0001", string shipmentId="SHIP-0001", string proof="POD-0001") =>
    new(id, shipmentId, DeliveryOutcome.Delivered, occurredAt, proof);
DeliveryEvent Exception(string id, string shipmentId, string reason) =>
    new(id, shipmentId, DeliveryOutcome.Exception, occurredAt, ExceptionReason:reason);
Check("Dispatched shipment registered", svc.RegisterShipment(shipment));
Check("Shipment identity retained", svc.GetShipment("SHIP-0001")?.SalesOrderId == "SO-0001");
Check("Shipment quantities preserved", svc.GetShipment("SHIP-0001")?.Lines.Sum(l=>l.Quantity)==5);
Check("Prepared shipment registered", svc.RegisterShipment(early));
Check("Pre-dispatch delivery blocked", svc.Record(Delivered("EARLY","SHIP-PREP")).Status==DeliveryResultStatus.Rejected);
Check("Unknown shipment rejected",svc.Record(Delivered("UNKNOWN","SHIP-MISSING")).Status==DeliveryResultStatus.Rejected);
Check("Missing proof rejected",svc.Record(Delivered("NO-PROOF",proof:" ")).Status==DeliveryResultStatus.Rejected);
var result=svc.Record(Delivered());
Console.WriteLine("DELIVERY RESULT " + JsonSerializer.Serialize(result));
Check("Delivery recorded",result.Status==DeliveryResultStatus.Recorded);
Check("Delivery confirmation retained",svc.GetOutcome("SHIP-0001")?.ProofReference=="POD-0001");
Check("Idempotent replay",svc.Record(Delivered()).Status==DeliveryResultStatus.Replayed);
Check("Changed proof rejected",svc.Record(Delivered(proof:"POD-OTHER")).Status==DeliveryResultStatus.Rejected);
Check("Different terminal event rejected",svc.Record(Exception("EXC-0001","SHIP-0001","Damaged")).Status==DeliveryResultStatus.Rejected);
Check("Original shipment quantities unchanged",svc.GetShipment("SHIP-0001")?.Lines.Sum(l=>l.Quantity)==5);
var other = new DispatchedShipment("SHIP-0002", "SO-0003", "WH-01", "Dispatched", lines);
Check("Second dispatched shipment registered",svc.RegisterShipment(other));
Check("Event ID cannot be reused",svc.Record(Delivered(shipmentId:"SHIP-0002")).Status==DeliveryResultStatus.Rejected);
Check("Exception reason required",svc.Record(Exception("EXC-0002","SHIP-0002"," ")).Status==DeliveryResultStatus.Rejected);
Check("Exception recorded",svc.Record(Exception("EXC-0002","SHIP-0002","Customer unavailable")).Status==DeliveryResultStatus.Recorded);
Check("Exception reason traceable",svc.GetOutcome("SHIP-0002")?.ExceptionReason=="Customer unavailable");
Check("Exception replay idempotent",svc.Record(Exception("EXC-0002","SHIP-0002","Customer unavailable")).Status==DeliveryResultStatus.Replayed);
Check("Delivery after exception rejected",svc.Record(Delivered("DEL-0002","SHIP-0002")).Status==DeliveryResultStatus.Rejected);
Check("Bad line quantity rejected",!svc.RegisterShipment(new("INVALID","SO-00","WH-01","Dispatched",new[]{new ShipmentLine("P000142",0)})));
Check("Duplicate shipment rejected",!svc.RegisterShipment(shipment));
Console.WriteLine($"RESULT: {passed}/{passed+failed} passed | {failed} failed");
Environment.ExitCode = failed == 0 ? 0 : 1;
