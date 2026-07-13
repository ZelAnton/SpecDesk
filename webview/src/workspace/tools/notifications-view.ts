/**
 * The central Notifications stub. It deliberately uses the same CentralFrame registration path as Start
 * and Document, so replacing this placeholder with live notifications later will not introduce a second
 * visibility/state system.
 */
export function buildNotificationsView(host: HTMLElement): void {
  const screen = document.createElement("section");
  screen.className = "notifications-screen";
  screen.setAttribute("aria-labelledby", "notifications-title");

  const title = document.createElement("h1");
  title.id = "notifications-title";
  title.className = "notifications-title";
  title.textContent = "Notifications";

  const list = document.createElement("ul");
  list.className = "notifications-list";
  list.setAttribute("aria-label", "Notifications");

  const placeholder = document.createElement("li");
  placeholder.className = "notifications-placeholder";
  placeholder.textContent = "Review requests and mentions will appear here.";
  list.appendChild(placeholder);

  screen.append(title, list);
  host.replaceChildren(screen);
}
