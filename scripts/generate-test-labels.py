"""Create exact Data Matrix fixtures: two valid v2 labels and one bad CRC routed to X."""
import json
from pathlib import Path
import zlib

import cv2
import numpy as np
import zxingcpp


def main():
    output = Path(__file__).resolve().parents[1] / 'labels' / 'version2-validation-test'
    output.mkdir(parents=True, exist_ok=True)
    sheet = np.full((850, 1650), 255, np.uint8)
    rows = []
    cards = []
    for index in range(3):
        body = f'2*{857 + index}*S/{2680 + index}*{1923 + index}*WH/OUT/{567 + index:05d}'
        checksum = f'{zlib.crc32(body.encode("ascii")):08X}'
        printed_crc = checksum if index < 2 else checksum[:-1] + ('0' if checksum[-1] != '0' else '1')
        payload = body + '*' + printed_crc
        expected = 'VERSION 2' if index < 2 else 'RAW X: CRC-32 mismatch'
        filename = f'{index + 1:02d}-' + ('valid' if index < 2 else 'invalid-checksum') + '.png'
        symbol = np.asarray(zxingcpp.write_barcode(zxingcpp.BarcodeFormat.DataMatrix, payload,
                                                   width=420, height=420, quiet_zone=16))
        cv2.imwrite(str(output / filename), symbol)
        decoded = zxingcpp.read_barcodes(symbol, formats=zxingcpp.BarcodeFormat.DataMatrix)
        assert len(decoded) == 1 and decoded[0].text == payload
        x = index * 550
        cv2.putText(sheet, f'LABEL {index + 1}', (x + 25, 50), cv2.FONT_HERSHEY_SIMPLEX, 1, 0, 2)
        cv2.putText(sheet, expected, (x + 25, 90), cv2.FONT_HERSHEY_SIMPLEX, .65, 0, 2)
        left = x + (550 - symbol.shape[1]) // 2
        sheet[115:115 + symbol.shape[0], left:left + symbol.shape[1]] = symbol
        lines = [f'Document: S/{2680 + index}', f'OUT: WH/OUT/{567 + index:05d}',
                 f'SO ID: {857 + index}   OUT ID: {1923 + index}',
                 f'Printed CRC: {printed_crc}', f'Correct CRC: {checksum}']
        for line_index, line in enumerate(lines):
            cv2.putText(sheet, line, (x + 25, 605 + line_index * 34), cv2.FONT_HERSHEY_SIMPLEX, .62, 0, 1)
        rows.append(dict(file=filename, payload=payload, expected=expected, correct_checksum=checksum))
        cards.append(f'<article><h2>Label {index + 1}: {expected}</h2><img src="{filename}" alt="Data Matrix label {index + 1}">'
                     f'<p>Document: S/{2680 + index}<br>OUT: WH/OUT/{567 + index:05d}<br>SO ID: {857 + index}; OUT ID: {1923 + index}</p>'
                     f'<p>Printed CRC: <code>{printed_crc}</code><br>Correct CRC: <code>{checksum}</code></p><code class="payload">{payload}</code></article>')
    decoded = zxingcpp.read_barcodes(sheet, formats=zxingcpp.BarcodeFormat.DataMatrix)
    assert {code.text for code in decoded} == {row['payload'] for row in rows}
    cv2.putText(sheet, 'TEST DATA - example IDs. Labels 1/2: validated version 2. Label 3: unvalidated X.',
                (25, 825), cv2.FONT_HERSHEY_SIMPLEX, .65, 0, 1)
    cv2.imwrite(str(output / 'three-labels.png'), sheet)
    (output / 'payloads.json').write_text(json.dumps(rows, indent=2) + '\n', encoding='utf-8')
    (output / 'print.html').write_text('''<!doctype html><html lang="en"><meta charset="utf-8">
<title>OEPS Scanner: version 2 validation test</title><style>
@page {size:A4 landscape; margin:12mm} body {font:14px Arial,sans-serif;color:#000;background:#fff;margin:0}
h1 {font-size:22px} main {display:flex;gap:5mm} article {border:1px solid #bbb;padding:4mm;flex:1;min-width:0;break-inside:avoid}
h2 {font-size:16px} img {display:block;width:48mm;height:auto;margin:3mm auto;image-rendering:pixelated}
.payload {display:block;overflow-wrap:anywhere;font-size:12px} p {line-height:1.5}
</style><h1>OEPS Scanner — two valid labels, one raw X fallback</h1>
<p>Test data with illustrative Odoo IDs. All three symbols decode; labels 1 and 2 produce version 2, while label 3 produces raw X.</p><main>'''
        + ''.join(cards) + '''</main><p>Label 3 changes only the last checksum character. Server diagnostics should report
“Raw X” with a CRC-32 mismatch reason. Clients requiring validated data should ignore X.
Every label version remaining visible can be sent again every 500 ms.
Print at 100% / actual size with no headers or footers. Preserve the white border around each symbol.</p></html>''', encoding='utf-8')
    print(json.dumps(rows, indent=2))
    print('PASS: individual symbols and combined sheet decode all three exact payloads.')


if __name__ == '__main__':
    main()
