import struct, zlib, os, sys

def read_png(path):
    """Read a PNG file and return width, height, channels, pixels, color_type."""
    with open(path, 'rb') as f:
        sig = f.read(8)
        if sig != b'\x89PNG\r\n\x1a\n':
            raise ValueError("Not a valid PNG")
        
        width = height = 0
        channels = 0
        raw_data = b''
        color_type = 0
        
        while True:
            chunk_len_bytes = f.read(4)
            if len(chunk_len_bytes) < 4:
                break
            chunk_len = struct.unpack('>I', chunk_len_bytes)[0]
            chunk_type = f.read(4)
            chunk_data = f.read(chunk_len)
            f.read(4)  # skip CRC
            
            if chunk_type == b'IHDR':
                width = struct.unpack('>I', chunk_data[0:4])[0]
                height = struct.unpack('>I', chunk_data[4:8])[0]
                ct = chunk_data[9]
                if ct == 2: channels = 3
                elif ct == 6: channels = 4
                elif ct == 0: channels = 1
                elif ct == 4: channels = 2
                elif ct == 3: channels = 1
                color_type = ct
            elif chunk_type == b'IDAT':
                raw_data += chunk_data
        
        raw_data = zlib.decompress(raw_data)
        
        stride = channels * width + 1  # +1 for filter byte
        pixels = []
        for row_start in range(0, len(raw_data), stride):
            filter_byte = raw_data[row_start]
            row_data = raw_data[row_start + 1:row_start + 1 + channels * width]
            pixels.append((filter_byte, row_data))
        
        return width, height, channels, pixels, color_type

def unfilter_sub(data, channels):
    for i in range(len(data)):
        a = data[i - channels] if i >= channels else 0
        data[i] = (data[i] + a) % 256
    return data

def unfilter_up(data, prev_row, channels):
    for i in range(len(data)):
        a = prev_row[i] if prev_row else 0
        data[i] = (data[i] + a) % 256
    return data

def unfilter_avg(data, prev_row, channels):
    for i in range(len(data)):
        a = data[i - channels] if i >= channels else 0
        b = prev_row[i] if prev_row else 0
        data[i] = (data[i] + (a + b) // 2) % 256
    return data

def unfilter_paeth(data, prev_row, channels):
    for i in range(len(data)):
        a = data[i - channels] if i >= channels else 0
        b = prev_row[i] if prev_row else 0
        c = (prev_row[i - channels] if prev_row and i >= channels else 0)
        p = a + b - c
        pa, pb, pc = abs(p - a), abs(p - b), abs(p - c)
        if pa <= pb and pa <= pc: pr = a
        elif pb <= pc: pr = b
        else: pr = c
        data[i] = (data[i] + pr) % 256
    return data

def decode_row(filter_byte, row_data, prev_row, channels):
    data = bytearray(row_data)
    if filter_byte == 0: pass
    elif filter_byte == 1: data = unfilter_sub(data, channels)
    elif filter_byte == 2: data = unfilter_up(data, prev_row, channels)
    elif filter_byte == 3: data = unfilter_avg(data, prev_row, channels)
    elif filter_byte == 4: data = unfilter_paeth(data, prev_row, channels)
    return bytes(data)

def resize_nearest(src_pixels, src_w, src_h, channels, tw, th):
    """Nearest-neighbor resize."""
    new = []
    for ty in range(th):
        sy = min(int(ty * src_h / th), src_h - 1)
        for tx in range(tw):
            sx = min(int(tx * src_w / tw), src_w - 1)
            idx = sy * src_w + sx
            pixel = src_pixels[idx]
            new.append(pixel)
    return new

def write_png(path, width, height, pixels):
    """Write a PNG file with RGBA pixels."""
    chunks = b'\x89PNG\r\n\x1a\n'
    
    # IHDR: 8-bit RGBA
    ihdr = struct.pack('>IIBBBBB', width, height, 8, 6, 0, 0, 0)
    chunks += make_chunk(b'IHDR', ihdr)
    
    # IDAT
    raw = b''
    for i in range(height):
        raw += b'\x00'  # filter None
        for j in range(width):
            idx = i * width + j
            r, g, b, a = pixels[idx]
            raw += struct.pack('BBBB', r, g, b, a)
    
    chunks += make_chunk(b'IDAT', zlib.compress(raw))
    chunks += make_chunk(b'IEND', b'')
    
    with open(path, 'wb') as f:
        f.write(chunks)

def make_chunk(ctype, data):
    chunk = ctype + data
    crc = struct.pack('>I', zlib.crc32(chunk) & 0xffffffff)
    return struct.pack('>I', len(data)) + chunk + crc

def create_template_icon(src_path, dst_path, target_size=22):
    """Create a grayscale template icon for macOS tray."""
    w, h, channels, pixels_raw, ct = read_png(src_path)
    print(f"Source: {w}x{h}, {channels} channels")
    
    # Decode all pixels
    src_pixels = []
    prev_row = None
    for filter_byte, row_data in pixels_raw:
        decoded = decode_row(filter_byte, row_data, prev_row, channels)
        prev_row = decoded
        for i in range(0, len(decoded), channels):
            if channels == 4:
                src_pixels.append((decoded[i], decoded[i+1], decoded[i+2], decoded[i+3]))
            elif channels == 3:
                src_pixels.append((decoded[i], decoded[i+1], decoded[i+2], 255))
    
    # Resize
    resized = resize_nearest(src_pixels, w, h, channels, target_size, target_size)
    
    # Convert to grayscale template: preserve alpha, make RGB grayscale
    template = []
    for r, g, b, a in resized:
        gray = int(0.299 * r + 0.587 * g + 0.114 * b)
        template.append((gray, gray, gray, a))
    
    write_png(dst_path, target_size, target_size, template)
    print(f"Created template icon: {target_size}x{target_size} -> {dst_path}")

if __name__ == '__main__':
    if len(sys.argv) >= 3:
        create_template_icon(sys.argv[1], sys.argv[2])
    else:
        create_template_icon("LlamaSwapManager.Desktop/Assets/llama.png", "LlamaSwapManager.Desktop/Assets/llama_tray_gray.png")
