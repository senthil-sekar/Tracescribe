#!/usr/bin/env python3
"""Turn failed tests in .trx files into GitHub Actions error annotations (visible in the PR/checks UI)."""
import glob
import sys
import xml.etree.ElementTree as ET

NS = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}


def escape(text: str) -> str:
    return text.replace("%", "%25").replace("\r", "").replace("\n", "%0A")


failed = 0
for path in glob.glob(sys.argv[1] + "/**/*.trx", recursive=True):
    root = ET.parse(path).getroot()
    for result in root.iterfind(".//t:UnitTestResult", NS):
        if result.get("outcome") != "Failed":
            continue
        failed += 1
        message = result.findtext(".//t:Message", default="", namespaces=NS)
        stack = result.findtext(".//t:StackTrace", default="", namespaces=NS)
        detail = (message + "\n" + "\n".join(stack.splitlines()[:6]))[:3000]
        print(f"::error title=Test failed: {result.get('testName')}::{escape(detail)}")

print(f"{failed} failed test(s)")
