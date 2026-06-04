import sys
from pathlib import Path

package_root = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(package_root / "src"))

from grace_sdk import GraceClient

client = GraceClient()

if not client.api_contract_version:
    raise SystemExit("GraceClient facade did not expose API contract metadata.")

if len(client.openapi_projection_sha256) != 64:
    raise SystemExit("GraceClient facade did not expose OpenAPI projection provenance.")

print(f"Python facade import OK ({client.api_contract_version}).")
