import { icon } from "../icons.js";
import {
  type RemoteReviewListConfig,
  type ReviewRequestsCallbacks,
  ReviewRequestsPanel,
} from "./review-requests-panel.js";

const PULL_REQUESTS_CONFIG: RemoteReviewListConfig = {
  id: "pullRequests",
  label: "Pull Requests",
  icon: icon("pullRequests"),
  ariaLabel: "Open pull requests involving you",
  authMessage: "Connect a GitHub account to see pull requests.",
  loadingMessage: "Loading pull requests…",
  emptyMessage: "You have no open pull requests.",
  errorMessage: "Couldn't load pull requests. Try again.",
  accepts: () => true,
};

/** Active (open) pull requests authored by or otherwise involving the connected user. */
export class PullRequestsPanel extends ReviewRequestsPanel {
  constructor(callbacks: ReviewRequestsCallbacks) {
    super(callbacks, PULL_REQUESTS_CONFIG);
  }
}
