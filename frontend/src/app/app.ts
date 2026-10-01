import { Component } from '@angular/core';
import { RouterOutlet } from '@angular/router';

/**
 * Root shell. Deliberately thin: it only hosts the router outlet so no component
 * logic is initialised before routing decides what to show.
 */
@Component({
  selector: 'app-root',
  imports: [RouterOutlet],
  template: '<router-outlet />',
})
export class App {}
