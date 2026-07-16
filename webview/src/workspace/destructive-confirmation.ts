/**
 * One two-step interaction for destructive actions. The first action only opens this inline prompt;
 * the supplied callback cannot run until the author explicitly presses the destructive confirmation.
 */

let confirmationSequence = 0;

export interface DestructiveConfirmationRequest {
  trigger: HTMLButtonElement;
  anchor: HTMLElement;
  title: string;
  description: string;
  onConfirm: () => void;
  /** Resolve a stable control after the destructive callback has synchronously updated its view. */
  focusAfterConfirm: () => HTMLElement | null;
}

export class DestructiveConfirmation {
  private prompt: HTMLElement | null = null;
  private trigger: HTMLButtonElement | null = null;
  private outsideListener: ((event: PointerEvent) => void) | null = null;

  open(request: DestructiveConfirmationRequest): void {
    this.close(false);

    const id = `destructive-confirmation-${++confirmationSequence}`;
    const descriptionId = `${id}-description`;
    const prompt = document.createElement("section");
    prompt.id = id;
    prompt.className = "destructive-confirmation";
    prompt.setAttribute("role", "group");
    prompt.setAttribute("aria-labelledby", `${id}-title`);
    prompt.setAttribute("aria-describedby", descriptionId);

    const title = document.createElement("strong");
    title.id = `${id}-title`;
    title.textContent = request.title;
    const description = document.createElement("p");
    description.id = descriptionId;
    description.textContent = request.description;
    const status = document.createElement("p");
    status.className = "destructive-confirmation-status";
    status.setAttribute("role", "status");
    status.setAttribute("aria-live", "polite");
    status.textContent = "Nothing has been deleted.";
    const confirm = document.createElement("button");
    confirm.type = "button";
    confirm.className = "destructive-confirmation-action";
    confirm.textContent = "Confirm deletion";
    confirm.addEventListener("click", (event) => {
      event.preventDefault();
      event.stopPropagation();
      if (this.prompt !== prompt) return;
      confirm.disabled = true;
      const run = request.onConfirm;
      this.close(false);
      try {
        run();
      } finally {
        this.focusAfterConfirmation(request, fallbackRoot);
      }
    });
    prompt.append(title, description, status, confirm);
    prompt.addEventListener("click", (event) => event.stopPropagation());
    prompt.addEventListener("keydown", (event) => {
      if (event.key === "Escape") {
        event.preventDefault();
        event.stopPropagation();
        this.close();
      }
    });

    const fallbackRoot =
      request.anchor.closest<HTMLElement>(".dock-tool, dialog, #central-frame") ?? document.body;
    request.trigger.setAttribute("aria-expanded", "true");
    request.trigger.setAttribute("aria-controls", id);
    request.trigger.setAttribute("aria-describedby", descriptionId);
    request.anchor.after(prompt);
    this.prompt = prompt;
    this.trigger = request.trigger;
    this.outsideListener = (event) => {
      const target = event.target;
      if (
        !(target instanceof Node) ||
        prompt.contains(target) ||
        request.trigger.contains(target)
      ) {
        return;
      }
      this.close();
    };
    document.addEventListener("pointerdown", this.outsideListener, true);
    confirm.focus();
  }

  close(restoreFocus = true): void {
    const trigger = this.trigger;
    if (this.outsideListener !== null) {
      document.removeEventListener("pointerdown", this.outsideListener, true);
    }
    this.outsideListener = null;
    this.prompt?.remove();
    this.prompt = null;
    this.trigger = null;
    if (trigger !== null) {
      trigger.setAttribute("aria-expanded", "false");
      trigger.removeAttribute("aria-controls");
      trigger.removeAttribute("aria-describedby");
      if (restoreFocus && trigger.isConnected) trigger.focus();
    }
  }

  private focusAfterConfirmation(
    request: DestructiveConfirmationRequest,
    fallbackRoot: HTMLElement,
  ): void {
    const preferred = request.focusAfterConfirm();
    const target = isFocusable(preferred)
      ? preferred
      : isFocusable(request.trigger)
        ? request.trigger
        : (firstFocusable(fallbackRoot) ?? firstFocusable(document.body));
    target?.focus();
  }
}

function isFocusable(element: HTMLElement | null): element is HTMLElement {
  return element?.isConnected === true && !element.matches(":disabled, [inert], [inert] *");
}

function firstFocusable(root: HTMLElement): HTMLElement | null {
  return root.querySelector<HTMLElement>(
    'input:not(:disabled), button:not(:disabled), textarea:not(:disabled), [tabindex="0"]',
  );
}
