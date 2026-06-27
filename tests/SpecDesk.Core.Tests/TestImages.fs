module SpecDesk.Core.Tests.TestImages

/// A minimal valid 2×2 RGBA PNG (real encoder output, so its chunk CRCs are correct), for
/// exercising the image pipeline without a binary fixture file.
let tinyPng: byte[] =
    System.Convert.FromBase64String
        "iVBORw0KGgoAAAANSUhEUgAAAAIAAAACCAYAAABytg0kAAAACXBIWXMAAA7EAAAOxAGVKw4bAAAAE0lEQVR4nGNhYPj/nwEIWBigAAAdYQIHX2OvnwAAAABJRU5ErkJggg=="
