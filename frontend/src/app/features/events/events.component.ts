import { ChangeDetectionStrategy, Component, OnDestroy, OnInit, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { Subject, Subscription } from 'rxjs';
import { takeUntil } from 'rxjs/operators';

import { FlashEvent } from '../../core/models/api.models';
import { ApiService } from '../../core/services/api.service';
import { AuthService } from '../../core/services/auth.service';
import { isCheckoutAllowed } from '../../core/services/jwt.util';

/**
 * Sale catalogue. Selecting an event routes the buyer into the waiting room, which
 * is the only path to checkout.
 */
@Component({
  selector: 'app-events',
  standalone: true,
  templateUrl: './events.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class EventsComponent implements OnInit, OnDestroy {
  private readonly api = inject(ApiService);
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);

  private readonly subscriptions = new Subscription();

  /** Completion signal for `takeUntil` (which accepts an Observable, not a Subscription). */
  private readonly destroyed$ = new Subject<void>();

  readonly events = signal<FlashEvent[]>([]);
  readonly loading = signal(true);
  readonly error = signal<string | null>(null);

  ngOnInit(): void {
    this.api
      .listEvents()
      .pipe(takeUntil(this.destroyed$))
      .subscribe({
        next: (events) => {
          this.events.set(events);
          this.loading.set(false);
        },
        error: () => {
          this.error.set('Could not load events. Is the API running on :5099?');
          this.loading.set(false);
        },
      });
  }

  ngOnDestroy(): void {
    this.destroyed$.next();
    this.destroyed$.complete();
    this.subscriptions.unsubscribe();
  }

  /**
   * Routes the buyer onward. If they were admitted by an earlier session there is
   * no reason to make them queue again, so they go straight to checkout.
   */
  enter(eventId: string): void {
    if (this.isCheckoutAllowed()) {
      void this.router.navigate(['/checkout'], { queryParams: { eventId } });
      return;
    }

    void this.router.navigate(['/waiting-room'], { queryParams: { eventId } });
  }

  /** Reads the CheckoutAllowed claim from the stored token. */
  isCheckoutAllowed(): boolean {
    return isCheckoutAllowed(this.auth.accessToken());
  }

  signOut(): void {
    this.auth.clear();
    void this.router.navigate(['/sign-in']);
  }

  formatPrice(minorUnits: number, currency: string): string {
    return new Intl.NumberFormat(undefined, {
      style: 'currency',
      currency: currency || 'GBP',
    }).format(minorUnits / 100);
  }
}