/**
 * Shapes shared with the ASP.NET Core API.
 *
 * Field names match the camelCase JSON produced by the backend's default
 * System.Text.Json / SignalR serializers exactly.
 */

/** Login/refresh response from `POST /api/auth/login`. */
export interface AuthResponse {
  accessToken: string;
  expiresAtUtc: string;
  userId: string;
  email: string;
  displayName: string;
  checkoutAllowed: boolean;
}

/** A sale as returned by `GET /api/events`. */
export interface FlashEvent {
  id: string;
  name: string;
  description: string;
  venue: string;
  startsAtUtc: string;
  priceInMinorUnits: number;
  currencyCode: string;
  admitBatchSize: number;
  checkoutWindowSeconds: number;
  totalSeats: number;
  seatsRemaining: number;
}

/** A seat line on an order. */
export interface OrderSeatDto {
  ticketId: string;
  seatNumber: string;
  unitPriceMinorUnits: number;
}

/** An order: a basket of seats plus how it was paid for. */
export interface OrderDto {
  orderId: string;
  eventId: string;
  status: 'Pending' | 'Paid' | 'Cancelled' | 'Expired';
  paymentMethod: string | null;
  seats: OrderSeatDto[];
  totalMinorUnits: number;
  currencyCode: string;
  expiresAtUtc: string;
  paidAtUtc: string | null;
}

/** Result of a batch hold: what was won and what was lost. */
export interface HoldSeatsResponse {
  order: OrderDto | null;
  heldSeats: OrderSeatDto[];
  unavailableSeats: string[];
}

/** Payload for settling an order. */
export interface PayOrderRequest {
  paymentMethod: string;
  cardholderName?: string;
}

/** A single seat in the seat map. */
export interface Seat {
  id: string;
  seatNumber: string;
  status: 'Available' | 'Reserved' | 'Sold';
}

/** Seat map for one sale, from `GET /api/events/{id}`. */
export interface EventDetail {
  id: string;
  name: string;
  description: string;
  venue: string;
  startsAtUtc: string;
  priceInMinorUnits: number;
  currencyCode: string;
  admitBatchSize: number;
  checkoutWindowSeconds: number;
  seats: Seat[];
}

/**
 * Payload of the `QueueStatusChanged` SignalR event pushed by
 * `QueueBackgroundWorker` and `QueueHub`.
 */
export interface QueueStatusChanged {
  eventId: string;
  status: 'Waiting' | 'Admitted' | 'Expired';
  /** 1-based place in line; null once admitted. */
  position: number | null;
  totalWaiting: number;
  /** Short-lived pass that unlocks checkout. Present only when admitted. */
  checkoutPassToken: string | null;
  passExpiresAtUtc: string | null;
  /** Server clock, used to correct for client drift when counting down. */
  serverUtc: string;
}

/** Broadcast to the whole waiting room on each worker tick. */
export interface QueueSnapshot {
  eventId: string;
  totalWaiting: number;
  serverUtc: string;
}

/** Result of `POST /api/bookings/reserve`. */
export interface ReserveTicketResponse {
  ticketId: string;
  seatNumber: string;
  status: 'Reserved' | 'Sold';
  holdExpiresAtUtc: string;
}

/** Uniform error body returned by the API. */
export interface ApiError {
  code: string;
  message: string;
  traceId?: string;
}

/** Decoded JWT payload. Claim keys vary, so it stays open-ended. */
export interface JwtPayload {
  sub?: string;
  email?: string;
  /** Custom claim: 'true' when admitted past the waiting room. */
  CheckoutAllowed?: string;
  event_id?: string;
  /** Seconds since epoch. */
  exp?: number;
  iat?: number;
  [claim: string]: unknown;
}