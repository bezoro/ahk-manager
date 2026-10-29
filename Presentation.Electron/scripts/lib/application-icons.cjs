const fs = require('node:fs');
const path = require('node:path');
const zlib = require('node:zlib');

/** Writes the package and tray icons from the same deterministic pixels. */
function writeApplicationIcons(output) {
  const size = 256,
    pixels = Buffer.alloc(size * (size * 4 + 1));
  for (let y = 0; y < size; y++)
    for (let x = 0; x < size; x++) {
      const offset = y * (size * 4 + 1) + 1 + x * 4;
      const px = (x * 64) / size,
        py = (y * 64) / size;
      const inside =
        px >= 3 &&
        px < 61 &&
        py >= 3 &&
        py < 61 &&
        !((px < 10 || px >= 54) && (py < 10 || py >= 54));
      const glyph =
        inside &&
        ((px >= 16 && px < 23 && py >= 15 && py < 49) ||
          (px >= 41 && px < 48 && py >= 15 && py < 49) ||
          (px >= 23 && px < 41 && py >= 28 && py < 35));
      pixels.set(glyph ? [255, 255, 255, 255] : inside ? [22, 115, 72, 255] : [0, 0, 0, 0], offset);
    }
  const crc = (data) => {
    let v = 0xffffffff;
    for (const b of data) {
      v ^= b;
      for (let i = 0; i < 8; i++) v = (v >>> 1) ^ (v & 1 ? 0xedb88320 : 0);
    }
    return (v ^ 0xffffffff) >>> 0;
  };
  const chunk = (type, data) => {
    const body = Buffer.concat([Buffer.from(type), data]),
      result = Buffer.alloc(data.length + 12);
    result.writeUInt32BE(data.length);
    body.copy(result, 4);
    result.writeUInt32BE(crc(body), result.length - 4);
    return result;
  };
  const ihdr = Buffer.alloc(13);
  ihdr.writeUInt32BE(size);
  ihdr.writeUInt32BE(size, 4);
  ihdr[8] = 8;
  ihdr[9] = 6;
  const png = Buffer.concat([
    Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]),
    chunk('IHDR', ihdr),
    chunk('IDAT', zlib.deflateSync(pixels)),
    chunk('IEND', Buffer.alloc(0)),
  ]);
  fs.writeFileSync(path.join(output, 'tray.png'), png);
  const ico = Buffer.alloc(22);
  ico.writeUInt16LE(1, 2);
  ico.writeUInt16LE(1, 4);
  ico[6] = size === 256 ? 0 : size;
  ico[7] = size === 256 ? 0 : size;
  ico.writeUInt16LE(1, 10);
  ico.writeUInt16LE(32, 12);
  ico.writeUInt32LE(png.length, 14);
  ico.writeUInt32LE(22, 18);
  fs.writeFileSync(path.join(output, 'app.ico'), Buffer.concat([ico, png]));
}

module.exports = { writeApplicationIcons };
