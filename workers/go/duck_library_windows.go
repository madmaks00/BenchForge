package main

import "syscall"

func openDuckLibrary(path string) (func(string) (uintptr, error), error) {
	lib, err := syscall.LoadDLL(path)
	if err != nil {
		return nil, err
	}
	return func(name string) (uintptr, error) {
		p, e := lib.FindProc(name)
		if e != nil {
			return 0, e
		}
		return p.Addr(), nil
	}, nil
}
