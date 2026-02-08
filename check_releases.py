import json
data = json.load(open('releases.json', encoding='utf-16'))
for r in data:
    tag = r['tag_name']
    if r['assets']:
        assets = ', '.join(f"{a['name']} ({a['size']/1048576:.1f} MB)" for a in r['assets'])
        print(f"{tag}: {assets}")
    else:
        print(f"{tag}: (no assets)")
