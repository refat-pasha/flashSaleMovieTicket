import { HubConnection, HubConnectionBuilder, HubConnectionState, LogLevel } from '@microsoft/signalr';
import { Injectable, inject } from '@angular/core';
import { BehaviorSubject, Observable, Subject, filter } from 'rxjs';

import { QueueSnapshot, QueueStatusChanged } from '../models/api.models';
import { AuthService } from './auth.service';

/** Base URL of the API; proxied in dev to avoid CORS noise. */
const API_BASE_URL = '';

/**
 * Real-time client for the backend `QueueHub`.
 *
 * Wraps the `@microsoft/signalr` connection in RxJS so components never touch the
 * library directly:
 * - `status$`  — hot stream of every `QueueStatusChanged` push.
 * - `snapshot$`— hot stream of whole-room counts, used for ambient UI.
 *
 * The connection is created lazily on the first `joinQueue()` and torn down by
 * `disconnect()`. Automatic reconnect is enabled because a dropped socket must not
 * silently strand a buyer in limbo.
 */
@Injectable({ providedIn: 'root' })
export class QueueSignalRService {
  private readonly auth = inject(AuthService);

  private connection: HubConnection | null = null;
  private startPromise: Promise<void> | null = null;

  /** Latest state pushed by the server, replayed to new subscribers. */
  private readonly _status = new BehaviorSubject<QueueStatusChanged | null>(null);

  readonly status$: Observable<QueueStatusChanged | null> = this._status.asObservable();

  /** Emits only when this buyer has been admitted. */
  readonly admitted$: Observable<QueueStatusChanged> = this.status$.pipe(
    filter((status): status is QueueStatusChanged => status?.status === 'Admitted'),
  );

  /** The most recent value, for components that only need current state. */
  readonly currentStatus: Observable<QueueStatusChanged | null> = this._status.asObservable();

  private readonly _snapshot = new Subject<QueueSnapshot>();

  readonly snapshot$ = this._snapshot.asObservable();

  private readonly _connected = new BehaviorSubject<boolean>(false);

  /** True while the socket is up. Drives the connection indicator in the UI. */
  readonly connected$: Observable<boolean> = this._connected.asObservable();

  /**
   * Ensures a live connection, then joins the waiting room for a sale.
   * Safe to call repeatedly; the second call re-uses the existing socket.
   */
  async joinQueue(eventId: string): Promise<void> {
    await this.ensureConnection();
    await this.connection!.invoke('JoinQueue', { eventId });
  }

  /** Voluntarily leaves the queue (e.g. the user navigated away). */
  async leaveQueue(eventId: string): Promise<void> {
    if (this.connection?.state === HubConnectionState.Connected) {
      await this.connection.invoke('LeaveQueue', eventId);
    }
  }

  /** Closes the socket. Call from the root component's teardown. */
  async disconnect(): Promise<void> {
    const connection = this.connection;
    if (!connection) {
      return;
    }

    this.connection = null;
    this.startPromise = null;
    this._connected.next(false);
    this._status.next(null);

    await connection.stop();
  }

  /** Creates and starts the connection exactly once, even under concurrent calls. */
  private async ensureConnection(): Promise<void> {
    if (this.connection?.state === HubConnectionState.Connected) {
      return;
    }

    // Reuse the in-flight start so parallel calls cannot open two sockets.
    if (this.startPromise) {
      return this.startPromise;
    }

    const token = this.auth.accessToken();
    if (!token) {
      throw new Error('Cannot join the queue without an access token.');
    }

    const connection = new HubConnectionBuilder()
      .withUrl(`${API_BASE_URL}/hubs/queue`, {
        // WebSockets cannot carry custom headers, so SignalR falls back to the
        // accessTokenFactory and passes ?access_token=, which JwtBearer reads for
        // the /hubs/queue path only (see Program.cs).
        accessTokenFactory: () => this.auth.accessToken() ?? '',
      })
      .withAutomaticReconnect([0, 2_000, 5_000, 10_000, 20_000])
      .configureLogging(LogLevel.Information)
      .build();

    connection.on('QueueStatusChanged', (payload: QueueStatusChanged) => {
      this._status.next(payload);
    });

    connection.on('QueueJoined', (payload: QueueStatusChanged) => {
      this._status.next(payload);
    });

    connection.on('QueueSnapshot', (payload: QueueSnapshot) => {
      this._snapshot.next(payload);
    });

    connection.onreconnecting(() => this._connected.next(false));
    connection.onreconnected(() => this._connected.next(true));
    connection.onclose(() => {
      this._connected.next(false);
      this.connection = null;
      this.startPromise = null;
    });

    this.connection = connection;
    this.startPromise = connection.start().then(() => {
      this._connected.next(true);
    });

    try {
      await this.startPromise;
    } catch (error) {
      // Allow a later retry rather than caching a rejected promise forever.
      this.startPromise = null;
      this.connection = null;
      throw error;
    }
  }
}