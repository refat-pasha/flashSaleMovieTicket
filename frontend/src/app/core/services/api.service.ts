import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, catchError, of } from 'rxjs';

import {
  AuthResponse,
  EventDetail,
  FlashEvent,
  HoldSeatsResponse,
  OrderDto,
  ReserveTicketResponse,
} from '../models/api.models';

/**
 * Thin typed wrapper over the REST API.
 *
 * Components depend on these methods rather than on raw URLs, so endpoint changes
 * stay in one place and every call is type-checked.
 */
@Injectable({ providedIn: 'root' })
export class ApiService {
  private readonly http = inject(HttpClient);

  // ---- Auth ----------------------------------------------------------------

  /**
   * Signs in. The first sign-in for an email registers the account; later sign-ins
   * must supply the same password.
   */
  login(email: string, displayName: string, password: string, eventId?: string): Observable<AuthResponse> {
    return this.http.post<AuthResponse>('/api/auth/login', {
      email,
      displayName,
      password,
      eventId: eventId ?? null,
    });
  }

  refresh(): Observable<AuthResponse> {
    return this.http.post<AuthResponse>('/api/auth/refresh', {});
  }

  // ---- Catalogue -----------------------------------------------------------

  listEvents(): Observable<FlashEvent[]> {
    return this.http.get<FlashEvent[]>('/api/events');
  }

  getEvent(eventId: string): Observable<EventDetail> {
    return this.http.get<EventDetail>(`/api/events/${eventId}`);
  }

  // ---- Waiting room --------------------------------------------------------

  /**
   * REST fallback for joining the queue. The preferred path is the SignalR hub;
   * this exists so a client can join and read its position without a live socket.
   */
  joinQueueViaRest(eventId: string): Observable<{ position: number | null }> {
    return this.http.post<{ position: number | null }>(`/api/events/${eventId}/queue`, {});
  }

  myPosition(eventId: string): Observable<{ position: number | null; totalWaiting: number }> {
    return this.http.get<{ position: number | null; totalWaiting: number }>(
      `/api/events/${eventId}/queue/me`,
    );
  }

  // ---- Booking -------------------------------------------------------------

  /**
   * Claims a seat. Rejects with `HttpErrorResponse`:
   * - 403 `checkout_not_allowed` — the waiting room has not admitted this buyer.
   * - 409 `ticket_taken`         — lost the race; another buyer won the seat.
   */
  // ---- Multi-seat orders -----------------------------------------------------

  /** Holds several seats at once and creates one order. Partial success is normal. */
  holdSeats(ticketIds: string[]): Observable<HoldSeatsResponse> {
    return this.http.post<HoldSeatsResponse>('/api/orders/hold', { ticketIds });
  }

  /** The buyer's live order for a sale, or null when they hold nothing. */
  currentOrder(eventId: string): Observable<OrderDto | null> {
    return this.http
      .get<OrderDto>('/api/orders/current', { params: { eventId } })
      .pipe(catchError(() => of(null)));
  }

  /** Settles an order. */
  payOrder(orderId: string, paymentMethod: string, cardholderName?: string): Observable<OrderDto> {
    return this.http.post<OrderDto>(`/api/orders/${orderId}/pay`, {
      paymentMethod,
      cardholderName: cardholderName ?? undefined,
    });
  }

  /** Abandons an order and returns its seats to the pool. */
  cancelOrder(orderId: string): Observable<void> {
    return this.http.post<void>(`/api/orders/${orderId}/cancel`, {});
  }

  // ---- Legacy single-seat booking ------------------------------------------

  /**
   * Claims one seat. Prefer `holdSeats`, which supports several seats at once.
   */
  reserveTicket(ticketId: string, fallbackTicketId?: string): Observable<ReserveTicketResponse> {
    return this.http.post<ReserveTicketResponse>('/api/bookings/reserve', {
      ticketId,
      fallbackTicketId: fallbackTicketId ?? null,
    });
  }

  /**
   * Confirms a held seat (stands in for capturing payment). Moves it to `Sold`.
   */
  confirmTicket(ticketId: string): Observable<ReserveTicketResponse> {
    return this.http.post<ReserveTicketResponse>('/api/bookings/confirm', { ticketId });
  }

  /** Releases a held seat back into the pool. */
  releaseTicket(ticketId: string): Observable<void> {
    return this.http.post<void>('/api/bookings/release', { ticketId });
  }

  /**
   * The buyer's current seat for a sale, so the checkout screen can restore state
   * after a refresh. Emits null when the buyer holds nothing.
   */
  mySeat(eventId: string): Observable<ReserveTicketResponse | null> {
    return this.http
      .get<ReserveTicketResponse>('/api/bookings/mine', { params: { eventId } })
      .pipe(catchError(() => of(null)));
  }
}