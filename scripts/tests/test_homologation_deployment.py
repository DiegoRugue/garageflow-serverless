import ast
import re
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
WORKFLOWS = ['deploy-serverless.yml']


class HomologationDeploymentTests(unittest.TestCase):
    def test_aws_deploy_requires_protected_branch_and_explicit_homologation_opt_in(self):
        for workflow in WORKFLOWS:
            document = (ROOT / ".github/workflows" / workflow).read_text(encoding="utf-8")
            block = re.search(r"(?ms)^  deploy:\n(.*?)(?=^  [\w-]+:|\Z)", document)
            self.assertIsNotNone(block, workflow)
            condition = re.search(r"(?m)^    if: (.+)$", block[1])[1]
            for ref in ("refs/heads/main", "refs/heads/develop", "refs/heads/codex/test", "refs/tags/main"):
                for flag in ("", "false", "true"):
                    with self.subTest(workflow=workflow, ref=ref, flag=flag):
                        expression = condition.replace("github.ref", repr(ref)).replace(
                            "vars.HOMOLOGATION_DEPLOY_ENABLED", repr(flag)
                        ).replace("&&", " and ").replace("||", " or ")
                        tree = ast.parse(expression, mode="eval")
                        allowed = (ast.Expression, ast.BoolOp, ast.And, ast.Or, ast.Compare, ast.Eq, ast.Constant)
                        self.assertTrue(all(isinstance(node, allowed) for node in ast.walk(tree)))
                        actual = eval(compile(tree, "<deploy-condition>", "eval"), {"__builtins__": {}})
                        expected = ref == "refs/heads/main" or (ref == "refs/heads/develop" and flag == "true")
                        self.assertEqual(expected, actual)


if __name__ == "__main__":
    unittest.main()
