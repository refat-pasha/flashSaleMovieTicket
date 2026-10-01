import { Injectable, signal } from '@angular/core';

const ACCESS_TOKEN_KEY = 'flashsale.accessToken';
const USER_KEY = 'flashsale.user';

/**
 * Owns the session token and the current buyer's profile.
 *
 * State is exposed as Angular signals so components can reactively read the token
 * without subscribing to an observable.
 *
 * Note on storage: `localStorage` is used because the brief specifies it, and it
 * keeps a buyer signed in across refreshes. It is readable by any script on the
 * origin, so the short-lived checkout pass is deliberately *not* stored here — it
 * is held in memory only and disappears on refresh, which is the desired trade-off.
 */
@Injectable({ providedIn: 'root' })
export class AuthService {
  private readonly _accessToken = signal<string | null>(readStorage(ACCESS_TOKEN_KEY));

  /** Current JWT, or null when signed out. */
  readonly accessToken = this._accessToken.asReadonly();

  private readonly _user = signal<AuthUser | null>(readJsonStorage<AuthUser>(USER_KEY));

  /** The signed-in buyer's profile, or null. */
  readonly user = this._user.asReadonly();

  readonly isAuthenticated = signal<boolean>(!!this._accessToken());

  /** Persists a successful login/refresh. */
  store(token: string, user: AuthUser, expiresAtUtc: string): void {
    this._accessToken.set(token);
    this.isAuthenticated.set(true);
    this._user.set(user);

    writeStorage(ACCESS_TOKEN_KEY, token);
    writeJsonStorage(USER_KEY, user);
    writeStorage('flashsale.expiresAtUtc', expiresAtUtc);
  }

  /** Replaces only the token, keeping the existing profile (used after refresh). */
  updateToken(token: string, checkoutAllowed: boolean, expiresAtUtc: string): void {
    this._accessToken.set(token);
    writeStorage(ACCESS_TOKEN_KEY, token);
    writeStorage('flashsale.expiresAtUtc', expiresAtUtc);

    if (checkoutAllowed && this._user()) {
      const updated = { ...this._user()!, checkoutAllowed };
      this._user.set(updated);
      writeJsonStorage(USER_KEY, updated);
    }
  }

  /** Clears all session state. */
  clear(): void {
    this._accessToken.set(null);
    this._user.set(null);
    this.isAuthenticated.set(false);

    removeStorage(ACCESS_TOKEN_KEY);
    removeStorage('flashsale.expiresAtUtc');
    removeJsonStorage(USER_KEY);
  }
}

/** The buyer's profile as returned by the API. */
export interface AuthUser {
  userId: string;
  email: string;
  displayName: string;
  checkoutAllowed: boolean;
}

function readStorage(key: string): string | null {
  try {
    return localStorage.getItem(key);
  } catch {
    // Private browsing / disabled storage: degrade to in-memory only.
    return null;
  }
}

function writeStorage(key: string, value: string): void {
  try {
    localStorage.setItem(key, value);
  } catch {
    /* Storage unavailable — the in-memory signal remains authoritative. */
  }
}

function removeStorage(key: string): void {
  try {
    localStorage.removeItem(key);
  } catch {
    /* No-op. */
  }
}

function readJsonStorage<T>(key: string): T | null {
  const raw = readStorage(key);
  if (!raw) {
    return null;
  }

  try {
    return JSON.parse(raw) as T;
  } catch {
    return null;
  }
}

function writeJsonStorage(key: string, value: unknown): void {
  writeStorage(key, JSON.stringify(value));
}

function removeJsonStorage(key: string): void {
  removeStorage(key);
}