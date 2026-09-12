import re
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
DEPLOY_WORKFLOW = ROOT / ".github" / "workflows" / "deploy-serverless.yml"


def job_block(document: str, job_name: str) -> str:
    match = re.search(
        rf"(?m)^  {re.escape(job_name)}:\n(?P<body>(?:    [^\n]*\n|\n)+?)(?=^  [A-Za-z0-9_-]+:|\Z)",
        document,
    )
    if match is None:
        raise AssertionError(f"workflow job {job_name} is missing")
    return match.group("body")


class DeploymentWorkflowPolicyTests(unittest.TestCase):
    def test_deploy_environment_is_selected_only_from_the_protected_ref(self):
        document = DEPLOY_WORKFLOW.read_text(encoding="utf-8")
        deploy = job_block(document, "deploy")
        protected_expression = "github.ref_name == 'main' && 'production' || 'homologation'"

        self.assertIn("if: github.ref == 'refs/heads/main' || github.ref == 'refs/heads/develop'", deploy)
        self.assertIn(f"environment: ${{{{ {protected_expression} }}}}", deploy)
        self.assertNotIn("environment: ${{ inputs.environment", deploy)
        self.assertIn(f"group: serverless-${{{{ {protected_expression} }}}}", document)
        self.assertNotIn("group: serverless-${{ inputs.environment", document)

    def test_trusted_cross_repository_edge_callers_inherit_deployment_secrets(self):
        document = DEPLOY_WORKFLOW.read_text(encoding="utf-8")
        expectations = {
            "deploy-edge-production": (
                "DiegoRugue/garageflow-infra-kubernetes/.github/workflows/deploy-edge.yml@main",
                "github.ref == 'refs/heads/main'",
            ),
            "deploy-edge-homologation": (
                "DiegoRugue/garageflow-infra-kubernetes/.github/workflows/deploy-edge.yml@main",
                "github.ref == 'refs/heads/develop'",
            ),
        }

        for job_name, (reusable_workflow, protected_ref_condition) in expectations.items():
            with self.subTest(job=job_name):
                block = job_block(document, job_name)
                self.assertIn("needs: deploy", block)
                self.assertIn(f"uses: {reusable_workflow}", block)
                self.assertIn(protected_ref_condition, block)
                self.assertIn("component: edge", block)
                self.assertRegex(block, r"(?m)^    secrets: inherit$")


if __name__ == "__main__":
    unittest.main()
