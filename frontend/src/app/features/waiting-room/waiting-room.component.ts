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
import { Subject, Subscription, timer } from 'rxjs';
import { finalize, switchMap, takeUntil, tap } from 'rxjs/operators';

import { QueueStatusChanged } from '../../core/models/api.models';
import { ApiService } from '../../core/services/api.service';
import { AuthService } from '../../core/services/auth.service';
import { QueueSignalRService } from '../../core/services/queue-signalr.service';

/**
 * The virtual waiting room.
 *
 * Subscribes to `QueueSignalRService.status$` and renders the buyer's live position.
 * When the server pushes `status === 'Admitted'` it:
 *   1. stores the short-lived checkout pass in sessionStorage,
 *   2. refreshes the session token so `CheckoutAllowed` becomes `"true"`,
 *   3. navigates to `/checkout`.
 *
 * All subscriptions are torn down in `ngOnDestroy`, and the socket is disconnected so
 * the backend frees the buyer's slot immediately when they navigate away.
 */
@Component({
  selector: 'app-waiting-room',
  standalone: true,
  templateUrl: './waiting-room.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class WaitingRoomComponent implements OnInit, OnDestroy {
  private readonly api = inject(ApiService);
  private readonly auth = inject(AuthService);
  private readonly queue = inject(QueueSignalRService);
  private readonly router = inject(Router);

  /** Owns every subscription created by this component. */
  private readonly subscriptions = new Subscription();

  /**
   * Completion signal for `takeUntil`. A `Subject` is required because
   * `takeUntil` accepts an Observable, not a Subscription.
   */
  private readonly destroyed$ = new Subject<void>();

  readonly eventId = signal<string | null>(null);
  readonly status = signal<'connecting' | 'waiting' | 'admitted' | 'error'>('connecting');
  readonly position = signal<number | null>(null);
  readonly totalWaiting = signal<number>(0);
  readonly connected = signal(false);
  readonly errorMessage = signal<string | null>(null);

  /** Seconds remaining on the checkout pass; null until admitted. */
  readonly secondsRemaining = signal<number | null>(null);

  /** True once admitted, while the countdown and navigation are in flight. */
  readonly isAdmitted = computed(() => this.status() === 'admitted');

  /** Progress through the queue: 100% when you are first in line. */
  readonly progressPercent = computed(() => {
    const total = this.totalWaiting();
    const current = this.position();

    if (!total || !current || total <= 0) {
      return 0;
    }

    return Math.round(((total - current + 1) / total) * 100);
  });
  ngOnInit(): void {
    const eventId = new URLSearchParams(window.location.search).get('eventId');

    if (eventId) {
      this.eventId.set(eventId);
    }

    // Live socket state, for the connection indicator.
    this.subscriptions.add(
      this.queue.connected$.subscribe((isConnected) => this.connected.set(isConnected)),
    );

    // The single source of truth for the view.
    this.subscriptions.add(
      this.queue.status$.subscribe((update) => {
        if (update) {
          this.applyUpdate(update);
        }
      }),
    );

    void this.start();
  }

  ngOnDestroy(): void {
    // Signal every `takeUntil` chain first, then release the socket subscriptions.
    this.destroyed$.next();
    this.destroyed$.complete();
    this.subscriptions.unsubscribe();

    // Free the slot server-side rather than waiting for a socket timeout.
    const eventId = this.eventId();

    if (eventId) {
      void this.queue.leaveQueue(eventId).finally(() => this.queue.disconnect());
    } else {
      void this.queue.disconnect();
    }
  }

  /** Opens the socket and joins the queue. */
  private async start(): Promise<void> {
    const eventId = this.eventId();

    if (!eventId) {
      this.status.set('error');
      this.errorMessage.set('No sale was selected. Pick an event to enter the queue.');
      return;
    }

    if (!this.auth.accessToken()) {
      await this.router.navigate(['/sign-in'], { queryParams: { eventId } });
      return;
    }

    try {
      this.status.set('connecting');
      await this.queue.joinQueue(eventId);
      this.status.set('waiting');
    } catch {
      // Socket unavailable: fall back to REST so the buyer still gets a position.
      this.fallbackToRest(eventId);
    }
  }

  /** One-shot REST join, used only when the WebSocket handshake fails. */
  private fallbackToRest(eventId: string): void {
    this.api
      .joinQueueViaRest(eventId)
      .pipe(takeUntil(this.destroyed$))
      .subscribe({
        next: (result) => {
          this.position.set(result.position);
          this.status.set(result.position ? 'waiting' : 'error');

          if (!result.position) {
            this.errorMessage.set('Could not join the waiting room. Please try again.');
          }
        },
        error: () => {
          this.status.set('error');
          this.errorMessage.set('Could not reach the waiting room. Is the API running?');
        },
      });
  }

  /** Applies one server push to the view. */
  private applyUpdate(update: QueueStatusChanged): void {
    this.totalWaiting.set(update.totalWaiting);
    this.position.set(update.position);

    if (update.status === 'Admitted' && update.checkoutPassToken) {
      this.status.set('admitted');
      this.startCountdown(update.passExpiresAtUtc);
      this.handleAdmission(update);
      return;
    }

    if (update.status === 'Waiting') {
      this.status.set('waiting');
    }
  }

  /**
   * Finalises admission: persist the pass, refresh the token so the guard sees
   * `CheckoutAllowed = "true"`, then move to checkout.
   */
  private handleAdmission(update: QueueStatusChanged): void {
    sessionStorage.setItem('flashsale.checkoutPass', update.checkoutPassToken!);

    this.api
      .refresh()
      .pipe(
        // Defer the store by a microtask so the signal update lands first.
        switchMap((response) => timer(0).pipe(tap(() => this.persistSession(response)))),
        takeUntil(this.destroyed$),
        // Navigate on both success and failure: the server has already admitted
        // this buyer, so blocking on a slow refresh would only strand them.
        finalize(() => {
          void this.router.navigate(['/checkout'], {
            queryParams: { eventId: update.eventId },
          });
        }),
      )
      .subscribe({ next: () => undefined, error: () => undefined });
  }

  private persistSession(response: {
    accessToken: string;
    expiresAtUtc: string;
    checkoutAllowed: boolean;
  }): void {
    this.auth.updateToken(response.accessToken, response.checkoutAllowed, response.expiresAtUtc);
  }

  /** Ticks the checkout-pass countdown once a second. */
  private startCountdown(expiresAt: string | null): void {
    if (!expiresAt) {
      return;
    }

    const deadline = new Date(expiresAt).getTime();

    const interval = timer(0, 1000)
      .pipe(takeUntil(this.destroyed$))
      .subscribe(() => {
        this.secondsRemaining.set(Math.max(0, Math.round((deadline - Date.now()) / 1000)));
      });

    this.subscriptions.add(interval);
  }

  /** Manual retry after an error. */
  retry(): void {
    this.errorMessage.set(null);
    this.status.set('connecting');
    void this.start();
  }

  /** Voluntary exit from the queue. */
  leave(): void {
    const eventId = this.eventId();

    if (eventId) {
      void this.queue.leaveQueue(eventId);
    }

    void this.router.navigate(['/events']);
  }
}