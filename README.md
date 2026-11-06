# $FrontierDistribution | Engineering solution

**Location:** `C:\Projects\$FrontierDistribution` (literal `$` character)  
**Research/knowledge SoS:** `S:\Brain-FrontierDistribution`  
**Governance:** `S:\_SB-Meta`

This is the **engineering solution workspace**, not a duplicate research brain. It starts intentionally minimal. Only real implementation activities should populate it: architecture alternatives, requirements derived from confirmed research, prototypes, source code, tests, integration contracts, and deployable artifacts.

The root `ID.SB.JSON` uses the existing SB marker convention:
- `sb_meta_root` is the actual meta governance path.
- `sos_nest_root` is a **custom informational field** resolving the external SoS domain. The supplied `Create_SBPackage.ps1` specification does **not** automatically package this reference; `peer_domains` is omitted rather than misused for cross-drive paths.

No ERP product, data schemas, interfaces, credentials, or internal Frontier processes are asserted by the project scaffold. Implementations must reference SoS source/evidence records and retain verification status.

`docs/Architecture-SWAG.md` is a candidate starting point only. It is not a production design.

Run the deployment package's `Test-FrontierSplit.ps1` after installation to verify initial hashes and linked sentinels. As the workspace changes, the original installation manifest becomes a baseline rather than a permanent state assertion.
