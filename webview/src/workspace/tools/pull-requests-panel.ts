import { icon } from "../icons.js";
import {
  type RemoteReviewListConfig,
  type ReviewRequestsCallbacks,
  ReviewRequestsPanel,
} from "./review-requests-panel.js";

const PULL_REQUESTS_CONFIG: RemoteReviewListConfig = {
  id: "pullRequests",
  label: "Change requests",
  icon: icon("pullRequests"),
  ariaLabel: "Open change requests involving you",
  authMessage: "Connect a GitHub account to see change requests.",
  loadingMessage: "Loading change requests…",
  emptyMessage: "You have no open change requests.",
  errorMessage: "Couldn't load change requests. Try again.",
  accepts: () => true,
};

/** Active (open) pull requests authored by or otherwise involving the connected user. */
export class PullRequestsPanel extends ReviewRequestsPanel {
  constructor(callbacks: ReviewRequestsCallbacks) {
    super(callbacks, PULL_REQUESTS_CONFIG);
  }
}
