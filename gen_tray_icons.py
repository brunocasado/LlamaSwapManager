import struct, zlib, os, sys

def read_png(path):
    with open(path, 'rb') as f:
        sig = f.read(8)
        if sig != b'\x89PNG\r\n\x1a\n':
            raise ValueError("Not a valid PNG")
        width = height = 0
        channels = 0
        raw_data = b''
        while True:
            chunk_len_bytes = f.read(4)
            if len(chunk_len_bytes) < 4: break
            chunk_len = struct.unpack('>I', chunk_len_bytes)[0]
            chunk_type = f.read(4)
            chunk_data = f.read(chunk_len)
            f.read(4)
            if chunk_type == b'IHDR':
                width = struct.unpack('>I', chunk_data[0:4])[0]
                height = struct.unpack('>I', chunk_data[4:8])[0]
                ct = chunk_data[9]
                if ct == 2: channels = 3
                elif ct == 6: channels = 4
                elif ct == 0: channels = 1
                elif ct == 4: channels = 2
                elif ct == 3: channels = 1
            elif chunk_type == b'IDAT':
                raw_data += chunk_data
        raw_data = zlib.decompress(raw_data)
        stride = channels * width + 1
        pixels = []
        for row_start in range(0, len(raw_data), stride):
            filter_byte = raw_data[row_start]
            row_data = raw_data[row_start + 1:row_start + 1 + channels * width]
            pixels.append((filter_byte, row_data))
        return width, height, channels, pixels

def decode_row(fb, rd, prev, ch):
    d = bytearray(rd)
    if fb == 1:
        for i in range(len(d)):
            a = d[i-ch] if i >= ch else 0
            d[i] = (d[i] + a) % 256
    elif fb == 2 and prev:
        for i in range(len(d)):
            d[i] = (d[i] + prev[i]) % 256
    elif fb == 3 and prev:
        for i in range(len(d)):
            a = d[i-ch] if i >= ch else 0
            d[i] = (d[i] + (a + prev[i]) // 2) % 256
    elif fb == 4 and prev:
        for i in range(len(d)):
            a = d[i-ch] if i >= ch else 0
            b2 = prev[i] if prev else 0
            c = (prev[i-ch] if prev and i >= ch else 0)
            p = a + b2 - c
            pa, pb, pc = abs(p-a), abs(p-b2), abs(p-c)
            pr = a if pa <= pb and pa <= pc else (b2 if pb <= pc else c)
            d[i] = (d[i] + pr) % 256
    return bytes(d)

def resize_bilinear(src, sw, sh, tw, th):
    new = []
    for ty in range(th):
        for tx in range(tw):
            fx = tx * sw / tw
            fy = ty * sh / th
            x0 = int(fx)
            y0 = int(fy)
            x1 = min(x0 + 1, sw - 1)
            y1 = min(y0 + 1, sh - 1)
            sx = fx - x0
            sy = fy - y0
            i00 = y0 * sw + x0
            i10 = y0 * sw + x1
            i01 = y1 * sw + x0
            i11 = y1 * sw + x1
            r = int(src[i00][0]*(1-sx)*(1-sy) + src[i10][0]*sx*(1-sy) + src[i01][0]*(1-sx)*sy + src[i11][0]*sx*sy)
            g = int(src[i00][1]*(1-sx)*(1-sy) + src[i10][1]*sx*(1-sy) + src[i01][1]*(1-sx)*sy + src[i11][1]*sx*sy)
            b = int(src[i00][2]*(1-sx)*(1-sy) + src[i10][2]*sx*(1-sy) + src[i01][2]*(1-sx)*sy + src[i11][2]*sx*sy)
            a = int(src[i00][3]*(1-sx)*(1-sy) + src[i10][3]*sx*(1-sy) + src[i01][3]*(1-sx)*sy + src[i11][3]*sx*sy)
            new.append((max(0,min(255,r)), max(0,min(255,g)), max(0,min(255,b)), max(0,min(255,a))))
    return new

def write_png(path, w, h, pixels):
    chunks = b'\x89PNG\r\n\x1a\n'
    ihdr = struct.pack('>IIBBBBB', w, h, 8, 6, 0, 0, 0)
    chunks += make_chunk(b'IHDR', ihdr)
    raw = b''
    for i in range(h):
        raw += b'\x00'
        for j in range(w):
            r,g,b,a = pixels[i*w+j]
            raw += struct.pack('BBBB', r, g, b, a)
    chunks += make_chunk(b'IDAT', zlib.compress(raw))
    chunks += make_chunk(b'IEND', b'')
    with open(path, 'wb') as f: f.write(chunks)

def make_chunk(ct, d):
    c = ct + d
    return struct.pack('>I', len(d)) + c + struct.pack('>I', zlib.crc32(c) & 0xffffffff)

def create_template(src_path, dst_path, size):
    w, h, ch, pixels_raw = read_png(src_path)
    src = []
    prev = None
    for fb, rd in pixels_raw:
        decoded = decode_row(fb, rd, prev, ch)
        prev = decoded
        for i in range(0, len(decoded), ch):
            if ch == 4: src.append((decoded[i], decoded[i+1], decoded[i+2], decoded[i+3]))
            elif ch == 3: src.append((decoded[i], decoded[i+1], decoded[i+2], 255))
    
    resized = resize_bilinear(src, w, h, size, size)
    template = [(int(0.299*r+0.587*g+0.114*b),)*3 + (a,) for r,g,b,a in resized]
    write_png(dst_path, size, size, template)
    print(f"  {size}x{size} -> {dst_path}")

if __name__ == '__main__':
    src = "LlamaSwapManager.Desktop/Assets/llama.png"
    if len(sys.argv) >= 3:
        src = sys.argv[1]
    create_template(src, "LlamaSwapManager.Desktop/Assets/llama_tray_gray_22.png", 22)
    create_template(src, "LlamaSwapManager.Desktop/Assets/llama_tray_gray_44.png", 44)
    print("Done.")
