import importlib.util
import inspect
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location('operator_header_review', ROOT / 'examples/governed-tools/review.py')
review = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(review)


class OperatorAccessHeaderTests(unittest.TestCase):
    def test_operator_key_is_separate_from_capability_and_global_bearer(self):
        self.assertIn('operator_key', inspect.signature(review.OperatorTransport).parameters)
        key = 'synthetic-operator-key-' + 'a' * 32
        transport = review.OperatorTransport('synthetic_capability', 'platform-bearer', key)
        self.assertEqual(transport.headers['X-Weave-Operator-Key'], key)
        self.assertEqual(transport.headers['X-Weave-Capability'], 'synthetic_capability')
        self.assertEqual(transport.headers['Authorization'], 'Bearer platform-bearer')

    def test_invalid_operator_headers_are_rejected_without_reflecting_key(self):
        self.assertIn('operator_key', inspect.signature(review.OperatorTransport).parameters)
        for key in ('short', 'x' * 257, 'x' * 32 + '\r\nInjected: yes', 'x' * 32 + ' '):
            with self.subTest(length=len(key)):
                with self.assertRaises(review.ReviewError) as failure:
                    review.OperatorTransport('synthetic_capability', operator_key=key)
                self.assertNotIn(key, str(failure.exception))

    def test_omitting_operator_key_preserves_existing_capability_transport(self):
        transport = review.OperatorTransport('synthetic_capability')
        self.assertNotIn('X-Weave-Operator-Key', transport.headers)
        self.assertNotIn('Authorization', transport.headers)


if __name__ == '__main__':
    unittest.main()
