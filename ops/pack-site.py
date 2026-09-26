"""Packs wwwroot\\report into a zip App Service can actually unpack.

PowerShell's Compress-Archive writes Windows separators into the entry names, and the Linux side of
App Service deploys with rsync, which refuses a path containing a backslash ("failed to stat
data\\answers.json"). Python writes the entry names itself, with forward slashes.
"""

import os
import zipfile

ROOT = r'C:\Temp\ForClaude\LLMQuorum\LLMQuorum.Web\wwwroot\report'
OUT = os.path.join(os.environ['TEMP'], 'llmquorum-report.zip')

with zipfile.ZipFile(OUT, 'w', zipfile.ZIP_DEFLATED) as archive:
    for folder, _, files in os.walk(ROOT):
        for name in files:
            full = os.path.join(folder, name)
            entry = os.path.relpath(full, ROOT).replace(os.sep, '/')
            archive.write(full, entry)

    entries = archive.namelist()

print('%d files, %.1f KB -> %s' % (len(entries), os.path.getsize(OUT) / 1024, OUT))

for entry in entries:
    print('  ' + entry)
