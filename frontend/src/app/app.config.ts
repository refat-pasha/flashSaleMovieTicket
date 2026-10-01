import {
  ApplicationConfig,
  provideBrowserGlobalErrorListeners,
} from '@angular/core';
import { provideHttpClient, withFetch, withInterceptors } from '@angular/common/http';
import { provideRouter, withComponentInputBinding } from '@angular/router';

import { routes } from './app.routes';
import { jwtInterceptor } from './core/interceptors/jwt.interceptor';

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),

    provideRouter(routes, withComponentInputBinding()),

    // Functional interceptors, applied in order. The JWT interceptor attaches the
    // bearer token to every outbound API call.
    provideHttpClient(withFetch(), withInterceptors([jwtInterceptor])),
  ],
};
