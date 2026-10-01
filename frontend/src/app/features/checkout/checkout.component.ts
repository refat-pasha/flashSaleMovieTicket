import { HttpErrorResponse } from '@angular/common/http';
import {
  ChangeDetectionStrategy,
  Component,
  OnDestroy,
  OnInit,
  computed,
  inject,
  signal,
} from '@angular/core';
import { Router } from '@angular/router';
import { Subject } from 'rxjs';
import { finalize, takeUntil } from 'rxjs/operators';

import { EventDetail, OrderDto, Seat } from '../../core/models/api.models';
import { ApiService } from '../../core/services/api.service';
import { AuthService } from '../../core/services/auth.service';

/** Venue-typical limit on seats in a single order. */
const MAX_SEATS = 6;

/**
 * Seat map where the buyer builds a basket of up to {@link MAX_SEATS} seats.
 *
 * Selecting several seats is what makes "four of us" a single flash-sale attempt.
 * Nothing is locked until the buyer commits, so they can change their mind freely.
 */
@Component({
  selector: 'app-checkout',
  standalone: true,
  templateUrl: './checkout.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class CheckoutComponent implements OnInit, OnDestroy {
  private readonly api = inject(ApiService);

  /** Exposed as a field so the template can show who is signed in. */
  readonly auth = inject(AuthService);

  private readonly router = inject(Router);

  /** Completion signal for `takeUntil` (which accepts an Observable, not a Subscription). */
  private readonly destroyed$ = new Subject<void>();

  readonly event = signal<EventDetail | null>(null);
  readonly loading = signal(true);
  readonly error = signal<string | null>(null);
  readonly notice = signal<string | null>(null);

  readonly eventId = signal<string | null>(null);

  /** Seats ticked in the map, not yet held. */
  readonly selected = signal<Seat[]>([]);

  /** The order created by holding those seats. Null until they commit. */
  readonly order = signal<OrderDto | null>(null);

  readonly working = signal(false);

  readonly maxSeats = MAX_SEATS;

  readonly seats = computed(() => this.event()?.seats ?? []);

  readonly takenCount = computed(
    () => this.seats().filter((s) => s.status !== 'Available').length,
  );

  readonly selectionCount = computed(() => this.selected().length);

  /** Running total of the ticked seats. */
  readonly selectionTotal = computed(() => {
    const price = this.event()?.priceInMinorUnits ?? 0;
    return this.selectionCount() * price;
  });

  readonly atMaxSeats = computed(() => this.selectionCount() >= MAX_SEATS);

  /** True once seats are held, so the map stops accepting input. */
  readonly hasOrder = computed(() => this.order() !== null);

  readonly isPaid = computed(() => this.order()?.status === 'Paid');

  ngOnInit(): void {
    const eventId = new URLSearchParams(window.location.search).get('eventId');

    if (!eventId) {
      this.error.set('No film was selected.');
      this.loading.set(false);
      return;
    }

    this.eventId.set(eventId);

    // Restore an order the buyer already holds, so a refresh keeps their seats.
    this.api
      .currentOrder(eventId)
      .pipe(takeUntil(this.destroyed$))
      .subscribe((order) => {
        if (order) {
          this.order.set(order);
          this.notice.set(
            order.status === 'Paid'
              ? `Order for ${order.seats.length} seat(s) is already paid.`
              : `You already hold ${order.seats.length} seat(s) for this film.`,
          );
        }
      });

    this.api
      .getEvent(eventId)
      .pipe(takeUntil(this.destroyed$))
      .subscribe({
        next: (detail) => {
          this.event.set(detail);
          this.loading.set(false);
        },
        error: () => {
          this.error.set('Could not load this film.');
          this.loading.set(false);
        },
      });
  }

  ngOnDestroy(): void {
    this.destroyed$.next();
    this.destroyed$.complete();
  }

  /** Email of the signed-in buyer, or null. */
  currentUserEmail(): string | null {
    return this.auth.user()?.email ?? null;
  }

  /** Toggles a seat in the basket. */
  toggleSeat(seat: Seat): void {
    if (this.hasOrder() || seat.status !== 'Available') {
      return;
    }

    const current = this.selected();
    const exists = current.some((s) => s.id === seat.id);

    if (exists) {
      this.selected.set(current.filter((s) => s.id !== seat.id));
      this.error.set(null);
      return;
    }

    if (this.atMaxSeats()) {
      this.error.set(`You can buy at most ${MAX_SEATS} seats in one order.`);
      return;
    }

    this.error.set(null);
    this.selected.set([...current, seat]);
  }

  isSelected(seat: Seat): boolean {
    return this.selected().some((s) => s.id === seat.id);
  }
    /** Holds every ticked seat and creates one order. */
  holdSelection(): void {
    const seats = this.selected();
    const eventId = this.eventId();

    if (seats.length === 0 || !eventId || this.working()) {
      return;
    }

    this.working.set(true);
    this.error.set(null);

    this.api
      .holdSeats(seats.map((s) => s.id))
      .pipe(
        takeUntil(this.destroyed$),
        finalize(() => this.working.set(false)),
      )
      .subscribe({
        next: (result) => {
          // Partial success is normal in a flash sale: report what was lost.
          if (!result.order) {
            this.error.set(
              result.unavailableSeats.length
                ? `Seat(s) ${result.unavailableSeats.join(', ')} were taken while you were choosing.`
                : 'Those seats could not be held.',
            );
            this.selected.set([]);
            this.refreshAvailability(eventId);
            return;
          }

          if (result.unavailableSeats.length > 0) {
            this.notice.set(
              `Seat(s) ${result.unavailableSeats.join(', ')} were taken, so they are not in your order.`,
            );
          }

          this.order.set(result.order);
          this.selected.set([]);
          this.refreshAvailability(eventId);
        },
        error: (err: unknown) => {
          this.error.set(this.describe(err, 'Could not hold those seats.'));
        },
      });
  }

  /** Releases the held order and returns its seats to the pool. */
  releaseOrder(): void {
    const order = this.order();
    const eventId = this.eventId();

    if (!order || order.status === 'Paid' || this.working()) {
      return;
    }

    this.working.set(true);
    this.error.set(null);

    this.api
      .cancelOrder(order.orderId)
      .pipe(
        takeUntil(this.destroyed$),
        finalize(() => this.working.set(false)),
      )
      .subscribe({
        next: () => {
          this.order.set(null);
          this.notice.set('Seats released.');
          if (eventId) {
            this.refreshAvailability(eventId);
          }
        },
        error: (err: unknown) => {
          this.error.set(this.describe(err, 'Could not release those seats.'));
        },
      });
  }

  /** Continues to the payment page. */
  goToPayment(): void {
    const order = this.order();
    if (!order) {
      return;
    }

    void this.router.navigate(['/payment'], {
      queryParams: { eventId: order.eventId, orderId: order.orderId },
    });
  }

  /** Leaves checkout. Always available, so the screen is never a dead end. */
  exit(): void {
    void this.router.navigate(['/events']);
  }

  /** Signs out and returns to sign-in. */
  signOut(): void {
    this.auth.clear();
    void this.router.navigate(['/sign-in']);
  }

  private refreshAvailability(eventId: string): void {
    this.api
      .getEvent(eventId)
      .pipe(takeUntil(this.destroyed$))
      .subscribe({
        next: (detail) => this.event.set(detail),
        error: () => undefined,
      });
  }

  private describe(err: unknown, fallback: string): string {
    if (!(err instanceof HttpErrorResponse)) {
      return fallback;
    }

    switch (err.error?.code) {
      case 'hold_expired':
        return 'The hold on these seats has expired. Please choose again.';
      case 'checkout_not_allowed':
        return 'Your checkout window has closed.';
      case 'seats_unavailable':
        return err.error.message;
      default:
        return err.status === 0 ? 'Cannot reach the server. Is the API running?' : fallback;
    }
  }

  /** Formats minor units for display, e.g. 1250 -> GBP 12.50. */
  formatPrice(minorUnits: number, currency: string): string {
    return new Intl.NumberFormat(undefined, {
      style: 'currency',
      currency: currency || 'GBP',
    }).format(minorUnits / 100);
  }
}