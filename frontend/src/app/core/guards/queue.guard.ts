import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';

import { AuthService } from '../services/auth.service';
import { decodeJwtPayload, isCheckoutAllowed } from '../services/jwt.util';

/**
 * Functional route guard protecting `/checkout`.
 *
 * Decodes the stored JWT and inspects the custom `CheckoutAllowed` claim:
 * - `"false"` or missing  -> redirect to `/waiting-room`
 * - `"true"`               -> allow through
 * - no token at all        -> redirect to `/sign-in`
 *
 * This is a UX convenience only. It stops users from reaching a screen they cannot
 * use, but it is trivially bypassable — `BookingController` re-checks the same claim
 * server-side, and that check is the actual security boundary.
 */
export const queueGuard: CanActivateFn = (route, state) => {
  const auth = inject(AuthService);
  const router = inject(Router);

  const token = auth.accessToken();

  if (!token) {
    // Remember where they were heading so sign-in can bounce them back.
    return router.createUrlTree(['/sign-in'], {
      queryParams: { returnUrl: state.url },
    });
  }

  const payload = decodeJwtPayload(token);
  const claim = payload?.CheckoutAllowed;

  if (claim === 'false') {
    // Blocked by the waiting room. Preserve the sale they were trying to reach.
    const eventId = route.queryParamMap.get('eventId');

    return router.createUrlTree(['/waiting-room'], {
      queryParams: eventId ? { eventId } : {},
    });
  }

  if (!isCheckoutAllowed(token)) {
    // Claim absent, malformed, or the token has expired.
    return router.createUrlTree(['/waiting-room'], {
      queryParams: { reason: 'expired' },
    });
  }

  // Admitted: let them through.
  return true;
};

/**
 * Guard for areas that merely require a signed-in buyer (the events list and the
 * waiting room itself). Keeps unauthenticated users out without imposing the
 * waiting-room check.
 */
export const authenticatedGuard: CanActivateFn = (route, state) => {
  const auth = inject(AuthService);
  const router = inject(Router);

  if (auth.accessToken()) {
    return true;
  }

  return router.createUrlTree(['/sign-in'], {
    queryParams: { returnUrl: state.url },
  });
};