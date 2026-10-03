/** Small images for the badge tests. Only the first bytes matter to the server, but the PNG is a real 1x1 image. */
export const PNG_BASE64 = 'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==';

export const PNG_BYTES = Buffer.from(PNG_BASE64, 'base64');

/** The start of a JPEG file: the signature is what the server checks. */
export const JPEG_BYTES = Buffer.from([0xff, 0xd8, 0xff, 0xe0, 0x00, 0x10, 0x4a, 0x46, 0x49, 0x46, 0x00, 0x01, 0xff, 0xd9]);

/** A RIFF header of a WebP file. */
export const WEBP_BYTES = Buffer.concat([Buffer.from('RIFF'), Buffer.from([0x0c, 0, 0, 0]), Buffer.from('WEBPVP8 '), Buffer.alloc(8)]);

export const SVG_BYTES = Buffer.from('<svg xmlns="http://www.w3.org/2000/svg" onload="alert(1)"/>');

export const imageInput = (bytes: Buffer, contentType: string) => ({ contentType, data: bytes.toString('base64') });
