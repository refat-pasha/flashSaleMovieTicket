import { Routes } from '@angular/router';

import { authenticatedGuard, queueGuard } from './core/guards/queue.guard';

/**
 * Route table.
 *
 * Guards are layered:
 *  - `authenticatedGuard` — needs any valid token.
 *  - `queueGuard`         — needs `CheckoutAllowed === "true"` in the JWT, i.e. the
 *                           buyer must have been admitted by the waiting room.
 *
 * Components are lazily loaded so the initial bundle stays small.
 */
export const routes: Routes = [
  {
    path: 'sign-in',
    loadComponent: () =>
      import('./features/events/sign-in.component').then((m) => m.SignInComponent),
    title: 'Sign in · Flash Sale',
  },
  {
    path: 'events',
    canActivate: [authenticatedGuard],
    loadComponent: () =>
      import('./features/events/events.component').then((m) => m.EventsComponent),
    title: 'Events · Flash Sale',
  },
  {
    path: 'waiting-room',
    canActivate: [authenticatedGuard],
    loadComponent: () =>
      import('./features/waiting-room/waiting-room.component').then(
        (m) => m.WaitingRoomComponent,
      ),
    title: 'Waiting room · Flash Sale',
  },
  {
    path: 'checkout',
    // The gate: no admission, no checkout.
    canActivate: [authenticatedGuard, queueGuard],
    loadComponent: () =>
      import('./features/checkout/checkout.component').then((m) => m.CheckoutComponent),
    title: 'Checkout · Flash Sale',
  },
  {
    path: 'payment',
    // Payment needs both a valid token and queue admission, same as checkout.
    canActivate: [authenticatedGuard, queueGuard],
    loadComponent: () =>
      import('./features/checkout/payment.component').then((m) => m.PaymentComponent),
    title: 'Payment · Flash Sale',
  },
  { path: '', pathMatch: 'full', redirectTo: 'events' },
  { path: '**', redirectTo: 'events' },
];
