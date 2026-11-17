//go:build !windows

package main

import "github.com/ebitengine/purego"

func openDuckLibrary(path string) (func(string) (uintptr, error), error) {
	lib, err := purego.Dlopen(path, purego.RTLD_NOW|purego.RTLD_GLOBAL)
	if err != nil {
		return nil, err
	}
	return func(name string) (uintptr, error) { return purego.Dlsym(lib, name) }, nil
}
