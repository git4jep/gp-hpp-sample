import { bootstrapApplication } from "@angular/platform-browser";
import {
  provideHttpClient,
  HttpClient,
  HttpErrorResponse,
} from "@angular/common/http";
import {
  Component,
  Injectable,
  inject,
  signal,
  computed,
  OnInit,
  OnDestroy,
} from "@angular/core";
import { CurrencyPipe } from "@angular/common";
import { FormsModule } from "@angular/forms";
import {
  provideRouter,
  RouterOutlet,
  RouterLink,
  ActivatedRoute,
  Router,
} from "@angular/router";
import { firstValueFrom } from "rxjs";

interface Product {
  id: string;
  name: string;
  category: string;
  price: number;
  art: string;
  description: string;
}
interface Config {
  merchant: string;
  mode: "demo" | "sandbox";
  currency: string;
  catalog: Product[];
  publicBaseUrl: string;
}
interface Order {
  id: string;
  mode: string;
  status: string;
  amount: number;
  currency: string;
  items: { name: string; price: number; quantity: number }[];
  transactionId: string | null;
  gpStatus: string | null;
  createdAt: string;
}
interface Checkout {
  order: Order;
  orderKey: string;
  hppUrl: string;
  callbacks: { return_url: string; cancel_url: string; status_url: string };
}
const errorMessage = (err: unknown): string =>
  err instanceof HttpErrorResponse
    ? err.error?.error ||
      "The merchant API is unavailable. Check that the C# backend is running."
    : "Something went wrong. Please try again.";

@Injectable({ providedIn: "root" })
class MerchantApi {
  private http = inject(HttpClient);
  config = signal<Config | null>(null);
  async load() {
    if (!this.config())
      this.config.set(
        await firstValueFrom(this.http.get<Config>("/api/config")),
      );
    return this.config()!;
  }
  checkout(items: { productId: string; quantity: number }[], email: string) {
    return firstValueFrom(
      this.http.post<Checkout>("/api/checkout", { items, email }),
    );
  }
  order(id: string, key: string, refresh = false) {
    const path = `/api/orders/${encodeURIComponent(id)}`;
    return firstValueFrom(
      refresh
        ? this.http.post<Order>(`${path}/reconcile`, {}, { params: { key } })
        : this.http.get<Order>(path, { params: { key } }),
    );
  }
  demo(id: string, key: string, result: string) {
    return firstValueFrom(
      this.http.post<{ redirectUrl: string }>(
        `/api/demo/orders/${encodeURIComponent(id)}/complete`,
        { result },
        { params: { key } },
      ),
    );
  }
}

@Component({
  selector: "app-root",
  standalone: true,
  imports: [RouterOutlet, RouterLink],
  template: `
    <div class="announcement">
      Thoughtful essentials. Everyday adventures.
      <span>Fictitious merchant · HPP integration sample</span>
    </div>
    <header class="site-header">
      <a routerLink="/" class="brand" aria-label="Northstar Supply home">
        <span class="brand-icon">✳</span
        ><span>NORTHSTAR<small>SUPPLY CO.</small></span>
      </a>
      <nav aria-label="Main navigation">
        <a routerLink="/">The collection</a
        ><span class="nav-note">Made for the everyday.</span>
      </nav>
      <span class="mode-pill"
        ><i></i
        >{{
          api.config()?.mode === "sandbox" ? "GP SANDBOX" : "LOCAL DEMO"
        }}</span
      >
    </header>
    <router-outlet />
    <footer>
      <a routerLink="/" class="footer-brand">NORTHSTAR SUPPLY CO.</a>
      <p>A little less ordinary. A little more considered.</p>
      <div>
        Angular storefront <span>·</span> ASP.NET Core API <span>·</span> Global
        Payments HPP
      </div>
      <small>Demonstration only. No real orders are fulfilled.</small>
    </footer>
  `,
})
class AppComponent {
  api = inject(MerchantApi);
}

@Component({
  standalone: true,
  imports: [CurrencyPipe, FormsModule],
  template: `
    <main>
      <section class="hero">
        <div>
          <p class="eyebrow"><span></span> LESS, BUT BETTER</p>
          <h1>Everyday,<br /><em>considered.</em></h1>
          <p class="hero-copy">
            Good things for the way you move.<br />Built with purpose. Carried
            with you.
          </p>
          <a href="#collection" class="text-link"
            >Explore the essentials <span>↘</span></a
          >
        </div>
        <div
          class="hero-art"
          aria-label="Illustration of Northstar outdoor essentials"
        >
          <div class="art-caption">
            THE EVERYDAY COLLECTION <span>01 / 03</span>
          </div>
          <div class="sun-disc"></div>
          <div class="hero-bag">
            <div class="bag-handles"></div>
            <div class="bag-pocket">N / S</div>
          </div>
          <div class="hero-cup"></div>
          <span class="art-stamp"
            >GO SOMEWHERE.<br />TAKE THE GOOD THINGS.</span
          >
        </div>
      </section>
      <section class="shop-layout" id="collection">
        <div class="collection">
          <div class="section-heading">
            <div>
              <p class="eyebrow">THE EDIT</p>
              <h2>Your next essentials.</h2>
            </div>
            <span>03 considered pieces</span>
          </div>
          <div class="products">
            @for (product of api.config()?.catalog ?? []; track product.id) {
              <article class="product">
                <div [class]="'product-art ' + product.art" aria-hidden="true">
                  <div class="object"><span>N / S</span></div>
                  <span class="product-index">0{{ $index + 1 }}</span>
                </div>
                <p class="product-category">{{ product.category }}</p>
                <div class="product-title">
                  <h3>{{ product.name }}</h3>
                  <span>{{ product.price / 100 | currency: currency() }}</span>
                </div>
                <p class="product-description">{{ product.description }}</p>
                <button
                  class="add-button"
                  (click)="add(product.id)"
                  [disabled]="quantity(product.id) >= 10"
                >
                  {{
                    quantity(product.id)
                      ? "Add another (" + quantity(product.id) + " in bag)"
                      : "Add to bag"
                  }}
                  <span>+</span>
                </button>
              </article>
            }
          </div>
          <div class="values">
            <span>Considered design</span><span>Built for daily use</span
            ><span>Secure hosted checkout</span>
          </div>
        </div>
        <aside class="checkout-card">
          <div class="cart-heading">
            <h2>Your bag</h2>
            <span>{{ count() }} {{ count() === 1 ? "item" : "items" }}</span>
          </div>
          @if (count() === 0) {
            <p class="empty">
              A little room for something good.<br />Add an essential to get
              started.
            </p>
          }
          @for (product of selected(); track product.id) {
            <div class="cart-line">
              <div>
                <strong>{{ product.name }}</strong>
                <div class="quantity">
                  <button
                    (click)="remove(product.id)"
                    [attr.aria-label]="'Remove one ' + product.name"
                  >
                    −
                  </button>
                  <span>{{ quantity(product.id) }}</span>
                  <button
                    (click)="add(product.id)"
                    [disabled]="quantity(product.id) >= 10"
                    [attr.aria-label]="'Add one ' + product.name"
                  >
                    +
                  </button>
                </div>
              </div>
              <span>{{
                (product.price * quantity(product.id)) / 100
                  | currency: currency()
              }}</span>
            </div>
          }
          <div class="shipping"><span>Shipping</span><span>On us</span></div>
          <div class="total">
            <span>Total</span
            ><strong
              >{{ total() / 100 | currency: currency() }}
              <small>{{ currency() }}</small></strong
            >
          </div>
          <form (ngSubmit)="checkout()">
            <label for="email">Email for your order</label>
            <input
              id="email"
              name="email"
              type="email"
              [(ngModel)]="email"
              required
              maxlength="100"
              placeholder="you@example.com"
              autocomplete="email"
            />
            <button
              class="primary checkout-button"
              [disabled]="busy() || !count() || !email"
              type="submit"
            >
              {{
                busy()
                  ? "Preparing your checkout…"
                  : "Continue to secure payment"
              }}
              <span>↗</span>
            </button>
          </form>
          @if (error()) {
            <p class="error" role="alert">{{ error() }}</p>
          }
          <p class="secure-note">
            <span>◇</span>
            {{
              api.config()?.mode === "sandbox"
                ? "Your payment happens on Global Payments’ secure hosted page. Card details never touch our site."
                : "Local demonstration mode. Try success, decline, or cancel—no card details or credentials needed."
            }}
          </p>
          <div class="payment-brand">POWERED BY <b>Global Payments</b></div>
        </aside>
      </section>
      <section class="flow-note">
        <span class="flow-icon">↗</span>
        <div>
          <h3>A smooth journey, there and back.</h3>
          <p>
            Secure payment on the hosted page. Automatic return to Northstar
            when you’re done—or when you cancel.
          </p>
        </div>
        <span class="flow-badge">HPP REDIRECT FLOW</span>
      </section>
    </main>
  `,
})
class StorefrontComponent implements OnInit {
  api = inject(MerchantApi);
  cart = signal<Record<string, number>>({});
  busy = signal(false);
  error = signal("");
  email = "";
  currency = computed(() => this.api.config()?.currency || "USD");
  selected = computed(() =>
    (this.api.config()?.catalog ?? []).filter((p) => this.quantity(p.id) > 0),
  );
  count = computed(() =>
    Object.values(this.cart()).reduce((sum, n) => sum + n, 0),
  );
  total = computed(() =>
    this.selected().reduce((sum, p) => sum + p.price * this.quantity(p.id), 0),
  );
  async ngOnInit() {
    try {
      await this.api.load();
    } catch (err) {
      this.error.set(errorMessage(err));
    }
  }
  quantity(id: string) {
    return this.cart()[id] || 0;
  }
  add(id: string) {
    if (this.quantity(id) < 10)
      this.cart.update((c) => ({ ...c, [id]: this.quantity(id) + 1 }));
  }
  remove(id: string) {
    this.cart.update((c) => ({
      ...c,
      [id]: Math.max(0, this.quantity(id) - 1),
    }));
  }
  async checkout() {
    if (this.busy() || !this.count()) return;
    this.busy.set(true);
    this.error.set("");
    try {
      const response = await this.api.checkout(
        this.selected().map((p) => ({
          productId: p.id,
          quantity: this.quantity(p.id),
        })),
        this.email,
      );
      // Only use the URL returned by the merchant backend, never construct an HPP URL.
      window.location.assign(response.hppUrl);
    } catch (err) {
      this.error.set(errorMessage(err));
      this.busy.set(false);
    }
  }
}

@Component({
  standalone: true,
  imports: [CurrencyPipe, RouterLink],
  template: `
    <main class="result-layout">
      <div class="breadcrumb">
        <a routerLink="/">Northstar Supply</a><span>/</span> Local payment
        simulator
      </div>
      <section class="result-card demo-card">
        <p class="eyebrow">LOCAL DEMO · NOT GLOBAL PAYMENTS</p>
        <h1>A checkout<br /><em>dress rehearsal.</em></h1>
        <p>
          This is a merchant-owned simulator, not the real HPP. It does not
          collect card details or make API calls to Global Payments.
        </p>
        @if (order(); as o) {
          <div class="demo-total">
            <span>Northstar Supply</span
            ><strong>{{ o.amount / 100 | currency: o.currency }}</strong>
          </div>
          <p class="muted">
            Choose an outcome to follow the same return and cancel callback
            routes used by the real integration.
          </p>
          <button
            class="primary"
            (click)="complete('success')"
            [disabled]="busy()"
          >
            Simulate successful payment <span>↗</span>
          </button>
          <button
            class="secondary"
            (click)="complete('declined')"
            [disabled]="busy()"
          >
            Simulate declined payment
          </button>
          <button
            class="cancel-link"
            (click)="complete('cancel')"
            [disabled]="busy()"
          >
            Cancel and return to Northstar
          </button>
        }
        @if (error()) {
          <p class="error" role="alert">{{ error() }}</p>
        }
      </section>
    </main>
  `,
})
class DemoComponent implements OnInit {
  api = inject(MerchantApi);
  route = inject(ActivatedRoute);
  order = signal<Order | null>(null);
  error = signal("");
  busy = signal(false);
  id = this.route.snapshot.paramMap.get("id") || "";
  key = this.route.snapshot.queryParamMap.get("key") || "";
  async ngOnInit() {
    try {
      const config = await this.api.load();
      if (config.mode !== "demo") {
        this.error.set("The simulator is disabled in sandbox mode.");
        return;
      }
      this.order.set(await this.api.order(this.id, this.key));
    } catch (err) {
      this.error.set(errorMessage(err));
    }
  }
  async complete(result: string) {
    if (this.busy()) return;
    this.busy.set(true);
    try {
      window.location.assign(
        (await this.api.demo(this.id, this.key, result)).redirectUrl,
      );
    } catch (err) {
      this.error.set(errorMessage(err));
      this.busy.set(false);
    }
  }
}

@Component({
  standalone: true,
  imports: [CurrencyPipe, RouterLink],
  template: `
    <main class="result-layout">
      <div class="breadcrumb">
        <a routerLink="/">Northstar Supply</a><span>/</span> Your order
      </div>
      <section class="result-card" aria-live="polite">
        <div [class]="'result-symbol ' + (approved() ? 'approved' : '')">
          {{ approved() ? "✓" : cancelled() ? "↶" : "◇" }}
        </div>
        <p class="eyebrow">
          {{
            order()?.mode === "demo"
              ? "LOCAL DEMONSTRATION"
              : "BACK AT NORTHSTAR"
          }}
        </p>
        <h1>{{ title() }}</h1>
        <p>{{ description() }}</p>
        @if (order(); as o) {
          <div class="receipt">
            @for (item of o.items; track $index) {
              <div>
                <span>{{ item.name }} × {{ item.quantity }}</span
                ><strong>{{
                  (item.price * item.quantity) / 100 | currency: o.currency
                }}</strong>
              </div>
            }
            <div class="receipt-total">
              <span>Order total</span
              ><strong>{{ o.amount / 100 | currency: o.currency }}</strong>
            </div>
            <div>
              <span>Payment status</span
              ><b class="status-tag">{{ o.status }}</b>
            </div>
            @if (o.gpStatus) {
              <div>
                <span>Provider status</span><code>{{ o.gpStatus }}</code>
              </div>
            }
            <div class="reference">
              <span>Order reference</span><code>{{ o.id }}</code>
            </div>
            @if (o.transactionId) {
              <div class="reference">
                <span>Transaction</span><code>{{ o.transactionId }}</code>
              </div>
            }
          </div>
          @if (o.mode === "demo") {
            <p class="demo-warning">
              Simulated outcome only. No payment was processed by Global
              Payments.
            </p>
          }
          @if (!approved() || o.status === "AUTHORIZED") {
            <button class="secondary" (click)="refresh()" [disabled]="busy()">
              {{ busy() ? "Checking payment…" : "Check payment status" }}
            </button>
          }
        }
        @if (error()) {
          <p class="error" role="alert">{{ error() }}</p>
        }
        <a class="primary button-link" routerLink="/"
          >Back to the collection <span>→</span></a
        >
        <p class="result-footnote">
          Your payment status comes from the merchant API, never from the
          address of this page.
        </p>
      </section>
    </main>
  `,
})
class ResultComponent implements OnInit, OnDestroy {
  api = inject(MerchantApi);
  route = inject(ActivatedRoute);
  router = inject(Router);
  order = signal<Order | null>(null);
  error = signal("");
  busy = signal(false);
  id = this.route.snapshot.queryParamMap.get("order") || "";
  key = this.route.snapshot.queryParamMap.get("key") || "";
  approved = computed(() =>
    ["PAID", "AUTHORIZED"].includes(this.order()?.status || ""),
  );
  cancelled = computed(() => this.order()?.status === "CANCELLED");
  title = computed(() =>
    !this.order()
      ? "Checking your order…"
      : this.order()?.status === "PAID"
        ? "Good things are on their way."
        : this.order()?.status === "AUTHORIZED"
          ? "Payment authorized."
          : this.cancelled()
            ? "Changed your mind?"
            : this.order()?.status === "DECLINED"
              ? "Let’s give that another try."
              : "A moment of patience.",
  );
  description = computed(() =>
    !this.order()
      ? "We’re retrieving the status from our merchant server."
      : this.order()?.status === "PAID"
        ? "Your payment has been confirmed. Thanks for shopping with Northstar."
        : this.order()?.status === "AUTHORIZED"
          ? "Your bank approved the payment authorization. Funds have not yet been confirmed as captured."
          : this.cancelled()
            ? "You cancelled the checkout and are back on the merchant website. This is not a refund or a guarantee that an in-flight payment cannot complete."
            : this.order()?.status === "DECLINED"
              ? "The payment was declined. You can return to the collection and start a new checkout."
              : "Your payment is not confirmed yet. Please check its status before starting another checkout.",
  );
  private timer?: ReturnType<typeof setTimeout>;
  private attempts = 0;
  async ngOnInit() {
    try {
      await this.api.load();
      this.order.set(await this.api.order(this.id, this.key));
      await this.refresh();
    } catch (err) {
      this.error.set(errorMessage(err));
    }
  }
  async refresh() {
    if (this.busy()) return;
    this.busy.set(true);
    this.error.set("");
    try {
      const order = await this.api.order(this.id, this.key, true);
      this.order.set(order);
      const page = this.approved()
        ? "success"
        : this.cancelled()
          ? "cancelled"
          : order.status === "DECLINED"
            ? "declined"
            : "pending";
      void this.router.navigate(["/payment", page], {
        queryParams: { order: this.id, key: this.key },
        replaceUrl: true,
      });
      if (order.status === "PENDING" && this.attempts++ < 5)
        this.timer = setTimeout(() => void this.refresh(), 5000);
    } catch (err) {
      this.error.set(errorMessage(err));
    } finally {
      this.busy.set(false);
    }
  }
  ngOnDestroy() {
    if (this.timer) clearTimeout(this.timer);
  }
}

bootstrapApplication(AppComponent, {
  providers: [
    provideHttpClient(),
    provideRouter([
      { path: "", component: StorefrontComponent, pathMatch: "full" },
      { path: "demo/checkout/:id", component: DemoComponent },
      { path: "payment/:outcome", component: ResultComponent },
      { path: "**", redirectTo: "" },
    ]),
  ],
}).catch(() => {
  document.body.textContent = "The storefront could not start. Please reload.";
});
