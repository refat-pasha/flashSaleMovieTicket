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
import { FormsModule } from '@angular/forms';
import { Subject, Subscription } from 'rxjs';
import { takeUntil } from 'rxjs/operators';

import { ApiService } from '../../core/services/api.service';
import { AuthService } from '../../core/services/auth.service';

/**
 * Sign-in screen. Stands in for a real identity provider, then sends the buyer
 * either to the events list or straight into a waiting room when an `eventId` was
 * carried through the query string.
 */
@Component({
  selector: 'app-sign-in',
  standalone: true,
  imports: [FormsModule],
  templateUrl: './sign-in.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SignInComponent implements OnInit, OnDestroy {
  private readonly api = inject(ApiService);
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);

  private readonly subscriptions = new Subscription();

  /** Completion signal for `takeUntil` (which accepts an Observable, not a Subscription). */
  private readonly destroyed$ = new Subject<void>();

  // Signals, not plain fields: this component is OnPush, so a computed() reading a
  // plain property would never invalidate and the disabled hint would never update.
  readonly email = signal('');
  readonly displayName = signal('');
  readonly password = signal('');
  readonly busy = signal(false);
  readonly error = signal<string | null>(null);

  /** True once a wrong password was rejected, so the form can say so. */
  readonly isReturning = signal(false);

  /** Client-side gate so an obviously-too-short password never hits the API. */
  readonly passwordTooShort = computed(() => {
    const value = this.password();
    return value.length > 0 && value.length < 8;
  });

  readonly canSubmit = computed(
    () =>
      !this.busy() &&
      this.email().trim().length > 0 &&
      this.displayName().trim().length > 0 &&
      this.password().length >= 8,
  );

  /** Sale the buyer came in for, carried through sign-in. */
  private eventId: string | null = null;

  ngOnInit(): void {
    this.eventId = new URLSearchParams(window.location.search).get('eventId');
    this.email.set(this.auth.user()?.email ?? '');
    this.displayName.set(this.auth.user()?.displayName ?? '');
  }

  ngOnDestroy(): void {
    this.destroyed$.next();
    this.destroyed$.complete();
    this.subscriptions.unsubscribe();
  }

  submit(): void {
    if (!this.canSubmit()) {
      return;
    }

    this.busy.set(true);
    this.error.set(null);

    this.api
      .login(
        this.email().trim(),
        this.displayName().trim(),
        this.password(),
        this.eventId ?? undefined,
      )
      .pipe(takeUntil(this.destroyed$))
      .subscribe({
        next: (response) => {
          this.auth.store(
            response.accessToken,
            {
              userId: response.userId,
              email: response.email,
              displayName: response.displayName,
              checkoutAllowed: response.checkoutAllowed,
            },
            response.expiresAtUtc,
          );

          const extras = this.eventId ? { queryParams: { eventId: this.eventId } } : {};

          // Already admitted in a previous session? Skip the queue entirely.
          if (response.checkoutAllowed) {
            void this.router.navigate(['/checkout'], extras);
            return;
          }

          void this.router.navigate([this.eventId ? '/waiting-room' : '/events'], extras);
        },
        error: (err: unknown) => {
          this.busy.set(false);

          const status = err instanceof HttpErrorResponse ? err.status : 0;
          const code = err instanceof HttpErrorResponse ? err.error?.code : null;

          if (status === 401 && code === 'invalid_credentials') {
            this.isReturning.set(true);
            this.error.set('That email and password do not match.');
            return;
          }

          if (status === 400) {
            this.error.set('Use a valid email and a password of at least 8 characters.');
            return;
          }

          this.error.set(
            status === 0 ? 'Cannot reach the server. Is the API running on port 5099?' : 'Sign-in failed.',
          );
        },
      });
  }
}