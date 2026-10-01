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
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import { Subject } from 'rxjs';
import { finalize, takeUntil } from 'rxjs/operators';

import { EventDetail, OrderDto } from '../../core/models/api.models';
import { ApiService } from '../../core/services/api.service';
import { AuthService } from '../../core/services/auth.service';

/** Payment methods the API accepts. */
type Method = 'Card' | 'PayPal' | 'ApplePay' | 'GooglePay';

interface MethodOption {
  id: Method;
  label: string;
  hint: string;
}

/**
 * Payment page reached after seats are held.
 *
 * Shows the order summary, lets the buyer choose a payment method, then settles the
 * order. On success it lands on the confirmation view rather than dumping the buyer
 * back at the seat map.
 */
@Component({
  selector: 'app-payment',
  standalone: true,
  imports: [FormsModule],
  templateUrl: './payment.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class PaymentComponent implements OnInit, OnDestroy {
  private readonly api = inject(ApiService);

  /** Exposed for the header. */
  readonly auth = inject(AuthService);

  private readonly router = inject(Router);

  private readonly destroyed$ = new Subject<void>();

  readonly methods: MethodOption[] = [
    { id: 'Card', label: 'Card', hint: 'Visa, Mastercard, Amex' },
    { id: 'PayPal', label: 'PayPal', hint: 'Pay with your PayPal balance' },
    { id: 'ApplePay', label: 'Apple Pay', hint: 'Confirm with Face ID' },
    { id: 'GooglePay', label: 'Google Pay', hint: 'Confirm with your device' },
  ];

  readonly order = signal<OrderDto | null>(null);
  readonly event = signal<EventDetail | null>(null);
  readonly loading = signal(true);
  readonly paying = signal(false);
  readonly error = signal<string | null>(null);
  readonly receipt = signal<OrderDto | null>(null);

  readonly eventId = signal<string | null>(null);
  readonly orderId = signal<string | null>(null);

  method = signal<Method>('Card');
  cardholderName = signal('');
  cardNumber = signal('');

  /** Simple client-side validation, computed from the signals above. */
  readonly cardDigits = computed(() => this.cardNumber().replace(/\D/g, ''));

  readonly cardLooksValid = computed(() => {
    if (this.method() !== 'Card') {
      return true;
    }

    const digits = this.cardDigits();
    return digits.length >= 12 && digits.length <= 19;
  });

  readonly canPay = computed(
    () => !this.paying() && this.order() !== null && this.cardLooksValid(),
  );

  ngOnInit(): void {
    const params = new URLSearchParams(window.location.search);
    const eventId = params.get('eventId');
    const orderId = params.get('orderId');

    if (!eventId || !orderId) {
      this.error.set('No order was selected.');
      this.loading.set(false);
      return;
    }

    this.eventId.set(eventId);
    this.orderId.set(orderId);

    // Reload the order so the summary is authoritative, not just route state.
    this.api
      .currentOrder(eventId)
      .pipe(takeUntil(this.destroyed$))
      .subscribe((order) => {
        if (!order || order.orderId !== orderId) {
          this.error.set('That order could not be found. Choose your seats again.');
          this.loading.set(false);
          return;
        }

        this.order.set(order);
        this.loading.set(false);
      });

    this.api
      .getEvent(eventId)
      .pipe(takeUntil(this.destroyed$))
      .subscribe({ next: (detail) => this.event.set(detail), error: () => undefined });
  }

  ngOnDestroy(): void {
    this.destroyed$.next();
    this.destroyed$.complete();
  }

  selectMethod(method: Method): void {
    this.method.set(method);
    this.error.set(null);
  }

  /** Groups the card number into fours as the buyer types. */
  onCardInput(value: string): void {
    const digits = value.replace(/\D/g, '').slice(0, 19);
    this.cardNumber.set(digits.replace(/(.{4})/g, '$1 ').trim());
  }

  pay(): void {
    const orderId = this.orderId();

    if (!orderId || !this.canPay()) {
      return;
    }

    this.paying.set(true);
    this.error.set(null);

    this.api
      .payOrder(orderId, this.method(), this.cardholderName().trim() || undefined)
      .pipe(
        takeUntil(this.destroyed$),
        finalize(() => this.paying.set(false)),
      )
      .subscribe({
        next: (paid) => {
          this.receipt.set(paid);
        },
        error: (err: unknown) => {
          this.error.set(this.describe(err, 'Payment failed. Nothing was charged.'));
        },
      });
  }

  backToSeats(): void {
    const eventId = this.eventId();
    void this.router.navigate(eventId ? ['/checkout'] : ['/events'], {
      queryParams: eventId ? { eventId } : {},
    });
  }

  done(): void {
    void this.router.navigate(['/events']);
  }

  private describe(err: unknown, fallback: string): string {
    if (!(err instanceof HttpErrorResponse)) {
      return fallback;
    }

    switch (err.error?.code) {
      case 'hold_expired':
        return 'The hold on these seats expired before payment. Nothing was charged.';
      case 'seats_unavailable':
        return err.error.message;
      case 'payment_conflict':
        return 'Those seats changed while we were taking payment. Nothing was charged.';
      case 'invalid_payment_method':
        return 'Choose a valid payment method.';
      default:
        return err.status === 0 ? 'Cannot reach the server. Is the API running?' : fallback;
    }
  }

  formatPrice(minorUnits: number, currency: string): string {
    return new Intl.NumberFormat(undefined, {
      style: 'currency',
      currency: currency || 'GBP',
    }).format(minorUnits / 100);
  }
}