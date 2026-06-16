#!/usr/bin/env python3
"""Generate a multi-resolution .ico file from llama.png for Windows icons."""

import struct
import zlib
import os
import sys

def read_png(path):
    """Read a PNG file and return (width, height, rgba_pixels)."""
    with open(path, 'rb') as f:
        sig = f.read(8)
        if sig != b'\x89PNG\r\n\x1a\n':
            raise ValueError("Not a valid PNG")
        
        width = height = 0
        channels = 0
        raw_data = b''
        
        while True:
            chunk_len_bytes = f.read(4)
            if len(chunk_len_bytes) < 4:
                break
            chunk_len = struct.unpack('>I', chunk_len_bytes)[0]
            chunk_type = f.read(4)
            chunk_data = f.read(chunk_len)
            f.read(4)  # CRC
            
            if chunk_type == b'IHDR':
                width = struct.unpack('>I', chunk_data[0:4])[0]
                height = struct.unpack('>I', chunk_data[4:8])[0]
                ct = chunk_data[9]
                if ct == 6:
                    channels = 4
                elif ct == 2:
                    channels = 3
                elif ct == 0:
                    channels = 1
                elif ct == 4:
                    channels = 2
            elif chunk_type == b'IDAT':
                raw_data += chunk_data
    
    # Decompress and decode
    raw_data = zlib.decompress(raw_data)
    pixels = []
    prev = None
    
    for row_start in range(0, len(raw_data), channels * width + 1):
        filter_byte = raw_data[row_start]
        row_data = raw_data[row_start + 1:row_start + 1 + channels * width]
        
        # Apply filter
        decoded = bytearray(row_data)
        for i in range(len(decoded)):
            if filter_byte == 0:
                pass
            elif filter_byte == 1:
                decoded[i] = (decoded[i] + (decoded[i - channels] if i >= channels else 0)) % 256
            elif filter_byte == 2:
                decoded[i] = (decoded[i] + (prev[i] if prev else 0)) % 256
            elif filter_byte == 3:
                decoded[i] = (decoded[i] + ((decoded[i - channels] if i >= channels else 0) + (prev[i] if prev else 0)) // 2) % 256
            elif filter_byte == 4:
                a = decoded[i - channels] if i >= channels else 0
                b = prev[i] if prev else 0
                c = (prev[i - channels] if prev and i >= channels else 0)
                p = a + b - c
                pa, pb, pc = abs(p - a), abs(p - b), abs(p - c)
                pr = a if pa <= pb and pa <= pc else (b if pb <= pc else c)
                decoded[i] = (decoded[i] + pr) % 256
        
        # Convert to RGBA
        for i in range(0, len(decoded), channels):
            if channels == 4:
                pixels.append((decoded[i], decoded[i+1], decoded[i+2], decoded[i+3]))
            elif channels == 3:
                pixels.append((decoded[i], decoded[i+1], decoded[i+2], 255))
            elif channels == 2:
                pixels.append((decoded[i], decoded[i+1], decoded[i+1], decoded[i+1]))
            else:
                pixels.append((decoded[i], decoded[i], decoded[i], 255))
        
        prev = bytes(decoded)
    
    return width, height, pixels


def resize_bilinear(src, sw, sh, tw, th):
    """Bilinear resize of pixel array."""
    new = []
    for ty in range(th):
        for tx in range(tw):
            fx = tx * sw / tw
            fy = ty * sh / th
            x0, y0 = int(fx), int(fy)
            x1, y1 = min(x0 + 1, sw - 1), min(y0 + 1, sh - 1)
            sx, sy = fx - x0, fy - y0
            i00 = y0 * sw + x0
            i10 = y0 * sw + x1
            i01 = y1 * sw + x0
            i11 = y1 * sw + x1
            r = int(src[i00][0] * (1-sx) * (1-sy) + src[i10][0] * sx * (1-sy) + src[i01][0] * (1-sx) * sy + src[i11][0] * sx * sy)
            g = int(src[i00][1] * (1-sx) * (1-sy) + src[i10][1] * sx * (1-sy) + src[i01][1] * (1-sx) * sy + src[i11][1] * sx * sy)
            b = int(src[i00][2] * (1-sx) * (1-sy) + src[i10][2] * sx * (1-sy) + src[i01][2] * (1-sx) * sy + src[i11][2] * sx * sy)
            a = int(src[i00][3] * (1-sx) * (1-sy) + src[i10][3] * sx * (1-sy) + src[i01][3] * (1-sx) * sy + src[i11][3] * sx * sy)
            new.append((max(0, min(255, r)), max(0, min(255, g)), max(0, min(255, b)), max(0, min(255, a))))
    return new


def pixels_to_png_bytes(pixels, w, h):
    """Encode pixels as a PNG data blob (for ICO image data)."""
    raw = b''
    for y in range(h):
        raw += b'\x00'  # No filter
        for x in range(w):
            r, g, b, a = pixels[y * w + x]
            raw += struct.pack('BBBB', r, g, b, a)
    
    # Minimal PNG: IHDR + IDAT + IEND
    ihdr = struct.pack('>IIBBBBB', w, h, 8, 6, 0, 0, 0)
    
    def make_chunk(ct, data):
        c = ct + data
        return struct.pack('>I', len(data)) + c + struct.pack('>I', zlib.crc32(c) & 0xffffffff)
    
    png = b'\x89PNG\r\n\x1a\n'
    png += make_chunk(b'IHDR', ihdr)
    png += make_chunk(b'IDAT', zlib.compress(raw))
    png += make_chunk(b'IEND', b'')
    return png


def write_ico(output_path, sizes):
    """Generate a multi-resolution .ico file."""
    # ICO Header: 6 bytes
    # Reserved (2) + Type=1 (icon, 2) + Count (2)
    header = struct.pack('<HHH', 0, 1, len(sizes))
    
    # Image directory entries + image data
    dir_entries = b''
    image_data = b''
    offset = 6 + len(sizes) * 16  # header + dir entries
    
    for size in sizes:
        # Read and resize source PNG
        src_w, src_h, src_pixels = read_png('LlamaSwapManager.Desktop/Assets/llama.png')
        resized = resize_bilinear(src_pixels, src_w, src_h, size, size)
        
        # Encode as PNG
        png_data = pixels_to_png_bytes(resized, size, size)
        
        # Directory entry (16 bytes)
        # ICO spec: width=0 means 256px
        w = 0 if size >= 256 else size
        h = 0 if size >= 256 else size
        entry = struct.pack('<BBBBHHII',
            w,                             # width (0 = 256)
            h,                             # height (0 = 256)
            0,                             # color palette (0 = no palette)
            0,                             # reserved
            1,                             # color planes
            32,                            # bits per pixel (32 = RGBA)
            len(png_data),                 # image data size
            offset                         # image data offset
        )
        dir_entries += entry
        image_data += png_data
        offset += len(png_data)
    
    with open(output_path, 'wb') as f:
        f.write(header)
        f.write(dir_entries)
        f.write(image_data)
    
    print(f"Generated {output_path} with sizes: {sizes}")


if __name__ == '__main__':
    output = sys.argv[1] if len(sys.argv) > 1 else 'LlamaSwapManager.Desktop/Assets/llama.ico'
    # Standard Windows icon sizes
    sizes = [16, 32, 48, 64, 128, 256]
    write_ico(output, sizes)
