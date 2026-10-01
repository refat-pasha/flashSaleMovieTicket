import { HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { Router } from '@angular/router';
import { catchError, throwError } from 'rxjs';

import { AuthService } from '../services/auth.service';

/**
 * Functional HTTP interceptor that attaches the bearer token to every outbound
 * API request.
 *
 * Design notes:
 * - Registered through `provideHttpClient(withInterceptors([...]))`, so it is a
 *   plain `HttpInterceptorFn` rather than a class.
 * - `req.clone` is used so the incoming `HttpRequest` stays immutable, which is
 *   what the Angular docs require when altering headers.
 * - Auth endpoints are skipped so a login attempt never carries a stale token.
 * - A 401 clears the session and redirects to sign-in; a 403 on the checkout path
 *   is left alone, because the waiting-room guard already handles that case.
 */
export const jwtInterceptor: HttpInterceptorFn = (req, next) => {
  const auth = inject(AuthService);
  const router = inject(Router);
  const token = auth.accessToken();

  // Only the anonymous sign-in endpoint must not carry a token. /api/auth/refresh is
  // [Authorize]-protected and DOES need the header, otherwise it 401s.
  const isAnonymousEndpoint = req.url.includes('/api/auth/login');

  // Only talk to our own API; never leak the token to third-party hosts.
  const isApiRequest = req.url.startsWith('/api') || req.url.includes('://localhost');

  const authorizedRequest =
    token && isApiRequest && !isAnonymousEndpoint
      ? req.clone({ setHeaders: { Authorization: `Bearer ${token}` } })
      : req;

  return next(authorizedRequest).pipe(
    catchError((error: unknown) => {
      // Signing in with bad credentials is not a session failure, so only sign-out
      // for genuine 401s elsewhere.
      if (error instanceof HttpErrorResponse && error.status === 401 && !isAnonymousEndpoint) {
        // The token is missing, expired or rejected: drop it and start over.
        auth.clear();
        void router.navigate(['/sign-in']);
      }

      // Re-throw so callers can map 403/409 onto their own UI states.
      return throwError(() => error);
    }),
  );
};