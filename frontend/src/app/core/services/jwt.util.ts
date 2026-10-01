import { JwtPayload } from '../models/api.models';

/**
 * Minimal, dependency-free JWT payload decoder.
 *
 * Deliberately does **not** validate the signature. That is the server's job and
 * cannot be done in a browser anyway — the API re-verifies every token with
 * JwtBearer. Decoding here is only so the UI can read claims and route the user
 * to the right screen.
 */
export function decodeJwtPayload(token: string | null | undefined): JwtPayload | null {
  if (!token) {
    return null;
  }

  const segments = token.split('.');
  if (segments.length !== 3) {
    return null;
  }

  try {
    const payload = base64UrlDecode(segments[1]);
    const parsed = JSON.parse(payload);
    return typeof parsed === 'object' && parsed !== null ? (parsed as JwtPayload) : null;
  } catch {
    // Malformed token: treat as absent rather than throwing during routing.
    return null;
  }
}

/**
 * True only when the token exists, carries `CheckoutAllowed === 'true'`, and has not
 * expired. Mirrors the server-side check in `BookingController`, which remains the
 * real security boundary.
 */
export function isCheckoutAllowed(token: string | null | undefined): boolean {
  const payload = decodeJwtPayload(token);
  if (!payload) {
    return false;
  }

  if (payload.CheckoutAllowed !== 'true') {
    return false;
  }

  return !isExpired(payload);
}

/** Seconds-until-expiry check with a small safety margin. */
export function isExpired(payload: JwtPayload, skewSeconds = 10): boolean {
  if (typeof payload.exp !== 'number') {
    return false;
  }

  const expiresAtMs = payload.exp * 1000;
  return Date.now() >= expiresAtMs - skewSeconds * 1000;
}

/** Decodes base64url (JWT flavour: '-' and '_', no padding) into a UTF-8 string. */
function base64UrlDecode(segment: string): string {
  const base64 = segment.replace(/-/g, '+').replace(/_/g, '/');
  const padded = base64.padEnd(base64.length + ((4 - (base64.length % 4)) % 4), '=');

  const binary = atob(padded);
  const bytes = Uint8Array.from(binary, (char) => char.charCodeAt(0));

  return new TextDecoder().decode(bytes);
}